using System.Diagnostics;
using GameShift.Contracts.Commands;
using GameShift.Contracts.Grpc;
using GameShift.Contracts.Protocol;
using GameShift.Core.Ipc;
using GameShift.Windows.Processes;
using GameShift.Windows.Services;
using Grpc.Core;

namespace GameShift.Windows.Ipc;

public sealed class ReadOnlyDiagnosticsGrpcService :
    GameShiftDiagnostics.GameShiftDiagnosticsBase
{
    private const int MaximumDiscoveryItems = 500;

    private readonly string _componentName;
    private readonly GrpcRequestValidator _requestValidator;
    private readonly IProcessInventory? _processInventory;
    private readonly IServiceInventory? _serviceInventory;

    public ReadOnlyDiagnosticsGrpcService(
        string componentName,
        RequestValidationPolicy validationPolicy,
        IProcessInventory? processInventory,
        IServiceInventory? serviceInventory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        _componentName = componentName.Trim();
        _requestValidator = new(validationPolicy);
        _processInventory = processInventory;
        _serviceInventory = serviceInventory;
    }

    public override Task<StatusReply> GetStatus(
        StatusRequest request,
        ServerCallContext context)
    {
        _requestValidator.Validate(
            request.Metadata,
            request.CalculateSize(),
            CommandKind.GetComponentStatus);

        return Task.FromResult(
            new StatusReply
            {
                Component = _componentName,
                State = _componentName == "SessionHost"
                    ? "Ready"
                    : "ReadOnly",
                ProtocolVersion = ProtocolInfo.CurrentVersion,
                Message = _componentName == "SessionHost"
                    ? "Discovery is read-only; explicitly approved "
                        + "background-application actions are enabled."
                    : "No system mutations are enabled.",
            });
    }

    public override Task<DiscoveryReply> Discover(
        DiscoveryRequest request,
        ServerCallContext context)
    {
        _requestValidator.Validate(
            request.Metadata,
            request.CalculateSize(),
            CommandKind.DiscoverSystem);

        if (request.MaximumItems is <= 0 or > MaximumDiscoveryItems)
        {
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    $"maximum_items must be between 1 and {MaximumDiscoveryItems}."));
        }

        DiscoveryReply reply = new();

        if (_processInventory is not null)
        {
            IReadOnlyList<ProcessSnapshot> processes = _processInventory.Capture();
            reply.TotalProcessCount = processes.Count;
            reply.Truncated |= processes.Count > request.MaximumItems;
            reply.Processes.AddRange(
                processes
                    .Take(request.MaximumItems)
                    .Select(ToRpc));
        }

        if (_serviceInventory is not null)
        {
            IReadOnlyList<ServiceSnapshot> services = _serviceInventory.Capture();
            reply.TotalServiceCount = services.Count;
            reply.Truncated |= services.Count > request.MaximumItems;
            reply.Services.AddRange(
                services
                    .Take(request.MaximumItems)
                    .Select(ToRpc));
        }

        return Task.FromResult(reply);
    }

    private static ProcessSummary ToRpc(ProcessSnapshot process) =>
        CreateProcessSummary(process);

    private static ProcessSummary CreateProcessSummary(
        ProcessSnapshot process)
    {
        ProcessClassification classification =
            ProcessClassificationService.Classify(
                process,
                Process.GetCurrentProcess().SessionId);
        return new()
        {
            ProcessId = process.ProcessId,
            Name = process.Name,
            StartedUnixMilliseconds =
                process.StartedAtUtc?.ToUnixTimeMilliseconds() ?? 0,
            ExecutablePath = process.ExecutablePath ?? string.Empty,
            SessionId = process.SessionId,
            WorkingSetBytes = process.WorkingSetBytes,
            TotalProcessorTimeMilliseconds =
                checked((long)process.TotalProcessorTime.TotalMilliseconds),
            HasMainWindow = process.HasMainWindow,
            SafetyClassification =
                classification.Kind.ToString(),
            ClassificationReason = classification.Reason,
            RecommendedAction =
                classification.RecommendedAction,
            PriorityClass = process.PriorityClass,
        };
    }

    private static ServiceSummary ToRpc(ServiceSnapshot service)
    {
        ServiceClassification classification =
            ServiceClassificationService.Classify(service);
        ServiceSummary summary = new()
        {
            ServiceName = service.ServiceName,
            DisplayName = service.DisplayName,
            Status = service.Status,
            CanStop = service.CanStop,
            ServiceType = service.ServiceType,
            StartType = service.StartType,
            SafetyClassification = classification.Kind.ToString(),
            ClassificationReason = classification.Reason,
            RecommendedAction = classification.RecommendedAction,
            IsDriver = service.IsDriver,
            IsSharedProcess = service.IsSharedProcess,
            TriggerCount = service.TriggerCount,
        };
        summary.Dependencies.AddRange(service.Dependencies);
        summary.RunningDependentServices.AddRange(
            service.RunningDependentServices);
        return summary;
    }
}
