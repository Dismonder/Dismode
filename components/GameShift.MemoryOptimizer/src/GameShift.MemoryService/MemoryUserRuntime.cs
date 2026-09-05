using System.Text.Json;
using GameShift.MemoryOptimizer.Core;
using GameShift.MemoryOptimizer.Core.Activity;
using GameShift.MemoryOptimizer.Core.Automation;
using GameShift.MemoryOptimizer.Core.Ipc;
using GameShift.MemoryOptimizer.Core.Models;
using GameShift.MemoryOptimizer.Core.Native;
using GameShift.MemoryOptimizer.Core.Optimization;
using GameShift.MemoryOptimizer.Core.Storage;

namespace GameShift.MemoryService;

internal sealed class MemoryUserRuntime : IAsyncDisposable
{
    private readonly MemoryOptimizerStore _store;
    private readonly WindowsMemoryMetricsSource _metrics = new();
    private readonly MemoryOptimizerEngine _engine;
    private readonly AutoOptimizationPolicy _policy = new();
    private readonly SystemCpuSampler _cpu = new();
    private OptimizationResult? _lastResult;

    internal MemoryUserRuntime(string userSid)
    {
        UserSid = userSid;
        _store = MemoryOptimizerStore.ForUserSid(userSid);
        _engine = new(
            new WindowsMemoryAreaOperations(userSid),
            _metrics,
            new WindowsGameActivityGuard(userSid));
    }

    internal string UserSid { get; }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        MemoryOptimizerSettings settings = await _store.GetSettingsAsync(
            cancellationToken).ConfigureAwait(false);
        await _store.SaveSettingsAsync(
            settings,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<OptimizationResult> history = await _store.GetHistoryAsync(
            1,
            cancellationToken).ConfigureAwait(false);
        _lastResult = history.Count > 0 ? history[0] : null;
        if (_lastResult is
            {
                State: OptimizationState.Completed or
                    OptimizationState.PartiallyCompleted,
            })
        {
            _policy.RecordCompleted(_lastResult.CompletedAtUtc);
        }

        WriteComponentStatus(settings);
    }

    internal async Task<MemoryOptimizerResponse> HandleAsync(
        MemoryOptimizerRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            object payload = request.Command switch
            {
                MemoryOptimizerCommand.GetStatus =>
                    await GetStatusAsync(cancellationToken).ConfigureAwait(false),
                MemoryOptimizerCommand.GetSettings =>
                    await _store.GetSettingsAsync(
                        cancellationToken).ConfigureAwait(false),
                MemoryOptimizerCommand.SaveSettings =>
                    await SaveSettingsAsync(
                        request.PayloadJson,
                        cancellationToken).ConfigureAwait(false),
                MemoryOptimizerCommand.Optimize =>
                    await OptimizeAsync(
                        request.PayloadJson,
                        cancellationToken).ConfigureAwait(false),
                MemoryOptimizerCommand.GetHistory =>
                    await GetHistoryAsync(
                        request.PayloadJson,
                        cancellationToken).ConfigureAwait(false),
                MemoryOptimizerCommand.PauseAutomation =>
                    await SetPausedAsync(
                        isPaused: true,
                        cancellationToken).ConfigureAwait(false),
                MemoryOptimizerCommand.ResumeAutomation =>
                    await SetPausedAsync(
                        isPaused: false,
                        cancellationToken).ConfigureAwait(false),
                MemoryOptimizerCommand.ShutdownTray =>
                    new ShutdownTrayReply(true),
                _ => throw new InvalidDataException(
                    "Unknown Memory Optimizer command."),
            };
            return Success(request, payload);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or
                ArgumentException)
        {
            return Failure(request, "invalid-request", exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(request, "access-denied", exception.Message);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            return Failure(request, "cancelled", "Request was cancelled.");
        }
    }

    internal async Task RunAutomationAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            MemoryOptimizerSettings settings = await _store.GetSettingsAsync(
                cancellationToken).ConfigureAwait(false);
            MemorySnapshot memory = _metrics.Capture();
            AutoOptimizationDecision decision = _policy.Evaluate(
                settings,
                memory,
                _cpu.Sample(),
                DateTimeOffset.UtcNow);
            if (decision.ShouldRun)
            {
                OptimizationResult result = await _engine.OptimizeAsync(
                    decision.Trigger,
                    decision.Areas,
                    settings,
                    cancellationToken).ConfigureAwait(false);
                _lastResult = result;
                if (result.State is not OptimizationState.Blocked and
                    not OptimizationState.AlreadyRunning)
                {
                    await _store.AddHistoryAsync(
                        result,
                        cancellationToken).ConfigureAwait(false);
                }

                if (result.State is OptimizationState.Completed or
                    OptimizationState.PartiallyCompleted)
                {
                    _policy.RecordCompleted(result.CompletedAtUtc);
                }
            }

            await Task.Delay(
                TimeSpan.FromSeconds(settings.PollIntervalSeconds),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _engine.Dispose();
        await _store.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<MemoryOptimizerStatus> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        MemoryOptimizerSettings settings = await _store.GetSettingsAsync(
            cancellationToken).ConfigureAwait(false);
        return new(
            settings.AutomationPaused,
            _engine.IsRunning,
            _metrics.Capture(),
            settings,
            _lastResult,
            MemoryOptimizerProduct.FileVersion,
            settings.AutomationPaused ? "Automation paused." : "Ready.");
    }

    private async Task<MemoryOptimizerSettings> SaveSettingsAsync(
        string payloadJson,
        CancellationToken cancellationToken)
    {
        MemoryOptimizerSettings settings =
            MemoryOptimizerProtocol.DeserializePayload<MemoryOptimizerSettings>(
                payloadJson) ??
            throw new InvalidDataException("Settings payload is missing.");
        settings = MemoryOptimizerSettings.Normalize(settings);
        await _store.SaveSettingsAsync(
            settings,
            cancellationToken).ConfigureAwait(false);
        WriteComponentStatus(settings);
        return settings;
    }

    private async Task<OptimizationResult> OptimizeAsync(
        string payloadJson,
        CancellationToken cancellationToken)
    {
        OptimizeRequestPayload request =
            MemoryOptimizerProtocol.DeserializePayload<OptimizeRequestPayload>(
                payloadJson) ??
            throw new InvalidDataException("Optimization payload is missing.");
        MemoryOptimizerSettings settings = await _store.GetSettingsAsync(
            cancellationToken).ConfigureAwait(false);
        OptimizationResult result = await _engine.OptimizeAsync(
            request.Trigger,
            request.Areas,
            settings,
            cancellationToken).ConfigureAwait(false);
        _lastResult = result;
        await _store.AddHistoryAsync(
            result,
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<IReadOnlyList<OptimizationResult>> GetHistoryAsync(
        string payloadJson,
        CancellationToken cancellationToken)
    {
        HistoryRequestPayload? request =
            MemoryOptimizerProtocol.DeserializePayload<HistoryRequestPayload>(
                payloadJson);
        return await _store.GetHistoryAsync(
            request?.MaximumCount ?? 100,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<MemoryOptimizerSettings> SetPausedAsync(
        bool isPaused,
        CancellationToken cancellationToken)
    {
        MemoryOptimizerSettings settings = await _store.GetSettingsAsync(
            cancellationToken).ConfigureAwait(false);
        settings = settings with { AutomationPaused = isPaused };
        await _store.SaveSettingsAsync(
            settings,
            cancellationToken).ConfigureAwait(false);
        WriteComponentStatus(settings);
        return settings;
    }

    private void WriteComponentStatus(MemoryOptimizerSettings settings)
    {
        ComponentStatusDocument status = new(
            1,
            settings.AutomationPaused,
            _engine.IsRunning,
            DateTimeOffset.UtcNow,
            MemoryOptimizerProduct.FileVersion);
        string targetPath = MemoryOptimizerStore.GetComponentStatusPath(
            UserSid);
        string temporaryPath = targetPath + ".tmp-" +
            Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    status,
                    MemoryOptimizerProtocol.JsonOptions));
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static MemoryOptimizerResponse Success(
        MemoryOptimizerRequest request,
        object payload) =>
        new(
            MemoryOptimizerProtocol.Version,
            request.RequestId,
            true,
            "ok",
            "Request completed.",
            JsonSerializer.Serialize(
                payload,
                MemoryOptimizerProtocol.JsonOptions));

    private static MemoryOptimizerResponse Failure(
        MemoryOptimizerRequest request,
        string code,
        string message) =>
        new(
            MemoryOptimizerProtocol.Version,
            request.RequestId,
            false,
            code,
            message,
            "{}");
}

internal sealed record OptimizeRequestPayload(
    OptimizationTrigger Trigger,
    MemoryArea Areas);

internal sealed record HistoryRequestPayload(int MaximumCount);

internal sealed record ShutdownTrayReply(bool ShutdownTray);
