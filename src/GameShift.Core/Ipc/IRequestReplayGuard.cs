namespace GameShift.Core.Ipc;

public interface IRequestReplayGuard
{
    ReplayRegistrationResult TryRegister(
        Guid requestId,
        DateTimeOffset observedAtUtc);
}

