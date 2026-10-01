using Dismode.Contracts.Protocol;
using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Recovery;
using Dismode.Windows.SystemOptimization;

namespace Dismode.IntegrationTests.SystemOptimization;

[TestClass]
public sealed class HibernateConfigurationActionTests
{
    [TestMethod]
    public async Task ActionUsesIndependentReadbackAndRestoresOriginalState()
    {
        FakeHibernateAdapter adapter = new(enabled: true);
        ActionId actionId = ActionId.Create();
        HibernateConfigurationAction action = new(
            actionId,
            desiredEnabled: false,
            adapter);
        ActionExecutionContext context = new(
            SessionId.Create(),
            actionId,
            IdempotencyKey.Create(),
            DateTimeOffset.UtcNow);

        PreparedAction<HibernateConfigurationState> prepared =
            await action.PrepareAsync(context, CancellationToken.None);
        ActionValidationResult validation = await action.ValidateAsync(
            context,
            prepared,
            CancellationToken.None);
        await action.ApplyAsync(context, prepared, CancellationToken.None);
        ActionVerificationResult applied = await action.VerifyAsync(
            context,
            prepared,
            CancellationToken.None);
        await action.CompensateAsync(context, prepared, CancellationToken.None);
        ActionVerificationResult restored =
            await action.VerifyCompensationAsync(
                context,
                prepared,
                CancellationToken.None);

        Assert.IsTrue(validation.IsValid);
        Assert.IsTrue(applied.MatchesExpectedState);
        Assert.IsTrue(restored.MatchesExpectedState);
        Assert.IsTrue(adapter.Enabled);
        Assert.HasCount(2, adapter.AppliedValues);
        Assert.IsFalse(adapter.AppliedValues[0]);
        Assert.IsTrue(adapter.AppliedValues[1]);
    }

    [TestMethod]
    public void RecoveryPreservesAnExternalThirdStateDecision()
    {
        HibernateConfigurationAction action = new(
            ActionId.Create(),
            desiredEnabled: false,
            new FakeHibernateAdapter(enabled: true));

        RestoreDecision<HibernateConfigurationState> decision =
            action.DecideRecovery(
                new(Enabled: true, IsSupported: true),
                new(Enabled: false, IsSupported: true),
                new(Enabled: false, IsSupported: false));

        Assert.AreEqual(
            RestoreDecisionKind.PreserveCurrentAndReportConflict,
            decision.Kind);
    }

    private sealed class FakeHibernateAdapter : IHibernateConfigurationAdapter
    {
        internal FakeHibernateAdapter(bool enabled)
        {
            Enabled = enabled;
        }

        internal bool Enabled { get; private set; }

        internal List<bool> AppliedValues { get; } = [];

        public ValueTask<HibernateConfigurationState> ReadAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new HibernateConfigurationState(Enabled, IsSupported: true));

        public ValueTask SetAsync(
            bool enabled,
            CancellationToken cancellationToken)
        {
            Enabled = enabled;
            AppliedValues.Add(enabled);
            return ValueTask.CompletedTask;
        }
    }
}
