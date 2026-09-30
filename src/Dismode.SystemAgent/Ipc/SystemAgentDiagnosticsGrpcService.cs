using Dismode.Contracts.Commands;
using Dismode.Contracts.Grpc;
using Dismode.Contracts.Protocol;
using Dismode.SystemAgent.Security;
using Dismode.Windows.Services;
using Grpc.Core;

namespace Dismode.SystemAgent.Ipc;

internal sealed class SystemAgentDiagnosticsGrpcService :
    DismodeDiagnostics.DismodeDiagnosticsBase
{
    private const int MaximumDiscoveryItems = 500;

    private readonly ISystemOptimizerGrpcAuthorization _authorization;
    private readonly IServiceInventory _serviceInventory;

    internal SystemAgentDiagnosticsGrpcService(
        ISystemOptimizerGrpcAuthorization authorization,
        IServiceInventory serviceInventory)
    {
        _authorization = authorization;
        _serviceInventory = serviceInventory;
    }

    public override async Task<StatusReply> GetStatus(
        StatusRequest request,
        ServerCallContext context)
    {
        _ = await _authorization.AuthorizeAsync(
                request.Metadata,
                request.CalculateSize(),
                CommandKind.GetComponentStatus,
                requiresMutation: false,
                context)
            .ConfigureAwait(false);
        return new()
        {
            Component = "SystemAgent",
            State = "Ready",
            ProtocolVersion = ProtocolInfo.CurrentVersion,
            Message = "Diagnostyka jest tylko do odczytu; mutacje wymagają zaufanego podpisu klienta.",
        };
    }

    public override async Task<DiscoveryReply> Discover(
        DiscoveryRequest request,
        ServerCallContext context)
    {
        _ = await _authorization.AuthorizeAsync(
                request.Metadata,
                request.CalculateSize(),
                CommandKind.DiscoverSystem,
                requiresMutation: false,
                context)
            .ConfigureAwait(false);
        if (request.MaximumItems is <= 0 or > MaximumDiscoveryItems)
        {
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    $"maximum_items must be between 1 and {MaximumDiscoveryItems}."));
        }

        IReadOnlyList<ServiceSnapshot> services = _serviceInventory.Capture();
        DiscoveryReply reply = new()
        {
            TotalServiceCount = services.Count,
            Truncated = services.Count > request.MaximumItems,
        };
        reply.Services.AddRange(
            services
                .Take(request.MaximumItems)
                .Select(ToRpc));
        return reply;
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
