using Dismode.Core.Actions;
using Dismode.Core.Devices;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Policies;
using Dismode.Windows.Power;

namespace Dismode.IntegrationTests.Power;

[TestClass]
public sealed class PowerPlanPolicyTests
{
    [TestMethod]
    public async Task PowerPlanActivationBlockedOnDesktopToPreserveActivePlan()
    {
        Guid activeScheme = Guid.NewGuid();
        Guid managedScheme = Guid.NewGuid();
        FakePowerSchemeAdapter adapter = new(activeScheme);
        FakeFormFactorDetector detector = new(DeviceFormFactor.Desktop);

        ActivateManagedPowerProfileAction action = new(
            ActionId.Create(),
            managedScheme,
            RecoveryAssurance.CompensationVerified,
            isOwnedByDismode: true,
            adapter: adapter,
            formFactorDetector: detector);

        ActionId actionId = ActionId.Create();
        ActionExecutionContext context = CreateContext(actionId);

        PreparedAction<PowerSchemeActionState> prepared =
            await action.PrepareAsync(context, CancellationToken.None);

        ActionValidationResult result =
            await action.ValidateAsync(context, prepared, CancellationToken.None);

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.BlockingReason, "desktop PCs");
    }

    [TestMethod]
    public async Task PowerPlanActivationAllowedOnLaptopWhenOwnedAndVerified()
    {
        Guid activeScheme = Guid.NewGuid();
        Guid managedScheme = Guid.NewGuid();
        FakePowerSchemeAdapter adapter = new(activeScheme);
        FakeFormFactorDetector detector = new(DeviceFormFactor.Laptop);

        ActivateManagedPowerProfileAction action = new(
            ActionId.Create(),
            managedScheme,
            RecoveryAssurance.CompensationVerified,
            isOwnedByDismode: true,
            adapter: adapter,
            formFactorDetector: detector);

        ActionId actionId = ActionId.Create();
        ActionExecutionContext context = CreateContext(actionId);

        PreparedAction<PowerSchemeActionState> prepared =
            await action.PrepareAsync(context, CancellationToken.None);

        ActionValidationResult result =
            await action.ValidateAsync(context, prepared, CancellationToken.None);

        Assert.IsTrue(result.IsValid, result.BlockingReason);
    }

    private static ActionExecutionContext CreateContext(ActionId actionId) =>
        new(
            SessionId.Create(),
            actionId,
            Dismode.Contracts.Protocol.IdempotencyKey.Create(),
            DateTimeOffset.UtcNow);

    private sealed class FakeFormFactorDetector : IDeviceFormFactorDetector
    {
        private readonly DeviceFormFactor _formFactor;

        public FakeFormFactorDetector(DeviceFormFactor formFactor) =>
            _formFactor = formFactor;

        public DeviceFormFactor Detect() => _formFactor;
    }

    private sealed class FakePowerSchemeAdapter : IPowerSchemeAdapter
    {
        private Guid _activeScheme;

        public FakePowerSchemeAdapter(Guid activeScheme) =>
            _activeScheme = activeScheme;

        public ValueTask<Guid> GetActiveSchemeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(_activeScheme);

        public ValueTask SetActiveSchemeAsync(Guid schemeId, CancellationToken cancellationToken)
        {
            _activeScheme = schemeId;
            return ValueTask.CompletedTask;
        }

        public ValueTask DuplicateSchemeAsync(Guid sourceSchemeId, Guid targetSchemeId, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteSchemeAsync(Guid schemeId, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<bool> SchemeExistsAsync(Guid schemeId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
    }
}
