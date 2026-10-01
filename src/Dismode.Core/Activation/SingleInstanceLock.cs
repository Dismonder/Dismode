using System.Security.Cryptography;
using System.Text;

namespace Dismode.Core.Activation;

/// <summary>
/// One Dismode component per user session, regardless of where its
/// executable lives. Two copies (a local release next to an installed one,
/// or the same shortcut clicked twice) used to start side by side and fight
/// over the same named pipes and journal; the second one now backs off.
/// <para>
/// A named mutex in the session namespace is shared by elevated and
/// unelevated processes of the same user, which is exactly the pair that
/// Dismode.UI and Dismode.SessionHost form. A held name is also refused
/// inside the same process, because a mutex lets the owning thread re-enter.
/// </para>
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private static readonly HashSet<string> HeldNames = new(
        StringComparer.Ordinal);

    private readonly Mutex _mutex;
    private readonly string _name;
    private bool _disposed;

    private SingleInstanceLock(Mutex mutex, string name)
    {
        _mutex = mutex;
        _name = name;
    }

    public static string BuildName(string component, string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        string fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(userSid.Trim())))[..20];
        return $"Local\\Dismode.{component}.{fingerprint}";
    }

    /// <summary>
    /// Returns the lock, or null when another instance already holds it.
    /// A lock abandoned by a crashed process counts as free.
    /// </summary>
    public static SingleInstanceLock? TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (HeldNames)
        {
            if (HeldNames.Contains(name))
            {
                return null;
            }

            Mutex mutex = new(initiallyOwned: false, name);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return null;
            }

            _ = HeldNames.Add(name);
            return new(mutex, name);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (HeldNames)
        {
            _ = HeldNames.Remove(_name);
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _mutex.Dispose();
    }
}
