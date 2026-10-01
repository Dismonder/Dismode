using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Dismode.UI.Services;

/// <summary>
/// Melduje, gdy inny proces dostaje okno pierwszego planu.
/// <para>
/// Gra, ktora wlasnie wystartowala, prawie zawsze wychodzi na pierwszy plan,
/// wiec to zdarzenie jest najtanszym sygnalem „sprawdz teraz", zamiast czekac
/// na kolejny takt odpytywania. Ten sam wzorzec stosuje HandheldCompanion
/// (hak EVENT_SYSTEM_FOREGROUND obok zapasowego licznika co 2 s); Playnite
/// zostaje przy samym odpytywaniu. Tu hak jest dodatkiem: takt zostaje, bo
/// gra bez okna albo uruchomiona za innym oknem nie wysle tego zdarzenia.
/// </para>
/// <para>
/// Hak jest poza kontekstem (bez wstrzykiwania DLL), wiec Windows dostarcza
/// wywolanie zwrotne przez petle komunikatow watku, ktory go zalozyl — tu
/// watku interfejsu. Delegat trzymany w polu, bo inaczej odbiorca smieci
/// zwolnilby go pod hakiem.
/// </para>
/// </summary>
public sealed class ForegroundWindowChangeListener : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;

    private readonly WinEventProcedure _procedure;
    private readonly Action<int> _onForegroundProcessChanged;
    private nint _hook;
    private int _lastProcessId;

    public ForegroundWindowChangeListener(
        Action<int> onForegroundProcessChanged)
    {
        _onForegroundProcessChanged = onForegroundProcessChanged
            ?? throw new ArgumentNullException(
                nameof(onForegroundProcessChanged));
        _procedure = OnWinEvent;
        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            nint.Zero,
            _procedure,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
        if (_hook == nint.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows nie założył haka zmiany okna pierwszego planu.");
        }
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThreadId,
        uint eventTimeMilliseconds)
    {
        if (eventType != EventSystemForeground || windowHandle == nint.Zero)
        {
            return;
        }

        _ = GetWindowThreadProcessId(windowHandle, out uint processId);
        if (processId == 0 || (int)processId == _lastProcessId)
        {
            // To samo okno co przed chwila (np. menu, dialog wlasny gry)
            // niczego nowego nie mowi.
            return;
        }

        _lastProcessId = (int)processId;
        _onForegroundProcessChanged((int)processId);
    }

    public void Dispose()
    {
        if (_hook != nint.Zero)
        {
            _ = UnhookWinEvent(_hook);
            _hook = nint.Zero;
        }

        GC.SuppressFinalize(this);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProcedure(
        nint hook,
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThreadId,
        uint eventTimeMilliseconds);

    // DllImport, nie LibraryImport: generator tego drugiego wymaga
    // niebezpiecznego kodu, ktorego projekt interfejsu nie wlacza.
#pragma warning disable SYSLIB1054
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint moduleHandle,
        WinEventProcedure procedure,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);
#pragma warning restore SYSLIB1054
}
