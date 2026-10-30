using System.Runtime.InteropServices;
using Dismode.Windows.NativeInterop;

namespace Dismode.Windows.Power;

internal sealed class WindowsExecutionStateInhibitor : IExecutionStateInhibitor
{
    private const nint InvalidHandle = -1;

    private readonly object _gate = new();
    private nint _request;
    private nint _reasonText;

    public bool TryAcquire(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (_request != 0)
            {
                return true;
            }

            // Napis zostaje w pamieci do zwolnienia zadania: system odwoluje
            // sie do niego przy wypisie zadan, a nie ma gwarancji kopii.
            nint text = Marshal.StringToHGlobalUni(reason);
            PowerReasonContext context = new()
            {
                Version = PowerNativeMethods.PowerRequestContextVersion,
                Flags = PowerNativeMethods.PowerRequestContextSimpleString,
                SimpleReasonString = text,
            };
            nint request = PowerNativeMethods.PowerCreateRequest(ref context);
            if (request == InvalidHandle || request == 0)
            {
                Marshal.FreeHGlobal(text);
                return false;
            }

            if (!PowerNativeMethods.PowerSetRequest(
                    request,
                    PowerNativeMethods.PowerRequestDisplayRequired)
                || !PowerNativeMethods.PowerSetRequest(
                    request,
                    PowerNativeMethods.PowerRequestSystemRequired))
            {
                Close(request);
                Marshal.FreeHGlobal(text);
                return false;
            }

            _request = request;
            _reasonText = text;
            return true;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_request == 0)
            {
                return;
            }

            Close(_request);
            Marshal.FreeHGlobal(_reasonText);
            _request = 0;
            _reasonText = 0;
        }
    }

    public void Dispose() => Release();

    private static void Close(nint request)
    {
        // Czyszczenie niezalezne od ustawienia: nieustawione zadanie to
        // zwykly blad, ktory ignorujemy, a zamkniecie uchwytu i tak zdejmuje
        // wszystko.
        _ = PowerNativeMethods.PowerClearRequest(
            request,
            PowerNativeMethods.PowerRequestDisplayRequired);
        _ = PowerNativeMethods.PowerClearRequest(
            request,
            PowerNativeMethods.PowerRequestSystemRequired);
        _ = PowerNativeMethods.CloseHandle(request);
    }
}
