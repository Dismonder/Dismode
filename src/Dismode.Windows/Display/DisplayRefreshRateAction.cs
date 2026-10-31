using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;

namespace Dismode.Windows.Display;

public sealed class DisplayRefreshRateAction : IReversibleAction<DisplayMode>
{
    private readonly IDisplayApi _api;
    private readonly DisplayMode? _original;

    private DisplayRefreshRateAction(ActionId id, DisplayMode target, IDisplayApi api, DisplayMode? original)
    {
        _api = api;
        _original = original;
        Target = target;
        Descriptor = new(id, SystemTargetKind.OperatingSystemSetting, target.DeviceName,
            OptimizationActionKind.SetDisplayRefreshRate);
    }

    public ActionDescriptor Descriptor { get; }
    public DisplayMode Target { get; }

    public static DisplayRefreshRateAction? TryCreate(ActionId id, IReadOnlyList<int> gameProcesses, IDisplayApi api)
    {
        if (!api.IsUserSession || api.FindGameMonitor(gameProcesses) is not string device)
        {
            return null;
        }
        return TryCreateForMonitor(id, device, api);
    }

    public static DisplayRefreshRateAction? TryCreateForMonitor(ActionId id, string device, IDisplayApi api)
    {
        if (!api.IsUserSession || api.GetCurrentMode(device) is not { RefreshRate: > 1 } current)
        {
            return null;
        }

        DisplayMode? best = api.GetModes(device)
            .Where(mode => mode with { RefreshRate = current.RefreshRate } == current)
            .OrderByDescending(mode => mode.RefreshRate).FirstOrDefault();
        return best is null || best.RefreshRate <= current.RefreshRate
            ? null : new(id, best, api, current);
    }

    public static DisplayRefreshRateAction ForRecovery(ActionId id, DisplayMode target, IDisplayApi api) =>
        new(id, target, api, null);

    public ValueTask<PreparedAction<DisplayMode>> PrepareAsync(ActionExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_original is null || Current() != _original)
        {
            throw new InvalidOperationException("Tryb ekranu zmienił się przed przygotowaniem.");
        }
        return ValueTask.FromResult(new PreparedAction<DisplayMode>(_original, Target, DateTimeOffset.UtcNow));
    }

    public ValueTask<ActionValidationResult> ValidateAsync(ActionExecutionContext context,
        PreparedAction<DisplayMode> preparedAction, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Current() == preparedAction.OriginalState
            ? ActionValidationResult.Allowed() : ActionValidationResult.Blocked("Tryb ekranu został zmieniony."));

    public ValueTask ApplyAsync(ActionExecutionContext context, PreparedAction<DisplayMode> preparedAction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Change(preparedAction.OriginalState, preparedAction.DesiredState);
        return ValueTask.CompletedTask;
    }

    public ValueTask<ActionVerificationResult> VerifyAsync(ActionExecutionContext context,
        PreparedAction<DisplayMode> preparedAction, CancellationToken cancellationToken) => Verify(preparedAction.DesiredState);

    public ValueTask<DisplayMode> ReadCurrentStateAsync(ActionExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Current());

    public ValueTask CompensateAsync(ActionExecutionContext context, PreparedAction<DisplayMode> preparedAction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Change(preparedAction.DesiredState, preparedAction.OriginalState);
        return ValueTask.CompletedTask;
    }

    public ValueTask<ActionVerificationResult> VerifyCompensationAsync(ActionExecutionContext context,
        PreparedAction<DisplayMode> preparedAction, CancellationToken cancellationToken) => Verify(preparedAction.OriginalState);

    private DisplayMode Current()
    {
        if (!_api.IsUserSession)
        {
            throw new InvalidOperationException("Przywracanie ekranu wymaga sesji użytkownika.");
        }
        return _api.GetCurrentMode(Target.DeviceName) ?? Missing();
    }

    private DisplayMode Missing() => new(Target.DeviceName, "", 0, 0, 0, 0);

    private void Change(DisplayMode expected, DisplayMode desired)
    {
        if (Current() != expected)
        {
            throw new InvalidOperationException("Tryb ekranu zmienił się przed zastosowaniem.");
        }
        _api.SetMode(desired);
    }

    private ValueTask<ActionVerificationResult> Verify(DisplayMode expected) =>
        ValueTask.FromResult(Current() == expected ? ActionVerificationResult.Verified()
            : ActionVerificationResult.Failed("Tryb ekranu jest inny niż zapisany."));
}
