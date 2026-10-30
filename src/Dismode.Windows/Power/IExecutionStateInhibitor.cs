namespace Dismode.Windows.Power;

/// <summary>
/// Keeps the display and the system awake for as long as it is held: a
/// Windows power request, the supported replacement for the old
/// SetThreadExecutionState. One instance serves one session.
/// <para>
/// The request belongs to the process that made it, so a crash of the host
/// releases it by itself and crash recovery has nothing to undo.
/// </para>
/// </summary>
public interface IExecutionStateInhibitor : IDisposable
{
    /// <summary>
    /// Takes the request with a human-readable reason (it shows in
    /// <c>powercfg /requests</c>). Idempotent while held. False when Windows
    /// refused: the session goes on without the inhibitor.
    /// </summary>
    bool TryAcquire(string reason);

    /// <summary>
    /// Clears the request and closes its handle. Idempotent; safe to call
    /// when nothing was ever acquired. <see cref="IDisposable.Dispose"/> does
    /// the same.
    /// </summary>
    void Release();
}
