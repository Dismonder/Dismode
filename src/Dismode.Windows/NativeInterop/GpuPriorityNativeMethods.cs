using System.Runtime.InteropServices;

namespace Dismode.Windows.NativeInterop;

/// <summary>
/// Klasa priorytetu szeregowania GPU procesu: to, ktora kolejka planisty
/// grafiki obsluguje jego polecenia, gdy gra i proces tla chca karty naraz.
/// <para>
/// Udokumentowane funkcje jadra grafiki z gdi32
/// (<c>D3DKMTGetProcessSchedulingPriorityClass</c> i jej odpowiednik
/// zapisujacy). Ustawienie jest wskazowka dla planisty GPU, nie izolacja:
/// polecenia juz wyslane do karty zostana dokonczone, a proces bez kontekstu
/// grafiki nie ma czego szeregowac. Jego wplyw na czas klatki nie jest
/// zmierzony.
/// </para>
/// <para>
/// Wartosci powyzej Normal wymagaja uprawnienia, ktorego nie mamy, wiec ta
/// biblioteka obniza i przywraca wylacznie w zakresie do Normal.
/// </para>
/// </summary>
internal static partial class GpuPriorityNativeMethods
{
    /// <summary>D3DKMT_SCHEDULINGPRIORITYCLASS_IDLE.</summary>
    internal const int Idle = 0;

    /// <summary>D3DKMT_SCHEDULINGPRIORITYCLASS_BELOW_NORMAL.</summary>
    internal const int BelowNormal = 1;

    /// <summary>D3DKMT_SCHEDULINGPRIORITYCLASS_NORMAL: wartosc domyslna.</summary>
    internal const int Normal = 2;

    internal const int StatusSuccess = 0;

    [LibraryImport("gdi32.dll")]
    internal static partial int D3DKMTGetProcessSchedulingPriorityClass(
        nint processHandle,
        out int priorityClass);

    [LibraryImport("gdi32.dll")]
    internal static partial int D3DKMTSetProcessSchedulingPriorityClass(
        nint processHandle,
        int priorityClass);
}
