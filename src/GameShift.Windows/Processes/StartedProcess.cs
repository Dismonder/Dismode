using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameShift.Windows.Processes;

/// <summary>
/// Proces uruchomiony przez <see cref="DesktopUserProcessStarter"/>: tyle,
/// ile potrzebuje kod uruchamiajacy (identyfikator i to, czy jeszcze
/// dziala), bez obietnic <see cref="Process"/>, ktorych nie da sie
/// dotrzymac dla dziecka cudzego rodzica.
/// </summary>
public sealed partial class StartedProcess : IDisposable
{
    private const uint WaitObject0 = 0;

    private readonly SafeProcessHandle? _handle;
    private readonly Process? _process;
    private bool _disposed;

    internal StartedProcess(int id, SafeProcessHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        Id = id;
        _handle = handle;
        InheritsParentToken = true;
    }

    internal StartedProcess(Process process, string? fallbackReason)
    {
        ArgumentNullException.ThrowIfNull(process);
        Id = process.Id;
        _process = process;
        FallbackReason = fallbackReason;
    }

    public int Id { get; }

    /// <summary>
    /// Prawda, gdy proces powstal jako dziecko wskazanego rodzica i
    /// odziedziczyl jego token; falsz, gdy wystartowal zwyczajnie z tokenem
    /// wywolujacego.
    /// </summary>
    public bool InheritsParentToken { get; }

    /// <summary>
    /// Dlaczego proces nie dostal tokenu pulpitu, choc wywolujacy dziala
    /// podniesiony; null, gdy dostal albo nie bylo takiej potrzeby.
    /// </summary>
    public string? FallbackReason { get; }

    public bool HasExited
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is not null)
            {
                return _process.HasExited;
            }

            return WaitForSingleObject(_handle!, 0) == WaitObject0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle?.Dispose();
        _process?.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(
        SafeProcessHandle handle,
        uint milliseconds);
}
