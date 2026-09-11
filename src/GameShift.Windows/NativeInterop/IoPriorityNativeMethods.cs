using System.Runtime.InteropServices;

namespace GameShift.Windows.NativeInterop;

/// <summary>
/// Odczyt i ustawienie priorytetu wejscia-wyjscia procesu.
/// <para>
/// To nie jest ten sam mechanizm, co priorytet procesora. Priorytet CPU jest
/// dla planisty wskazowka, ktora przy wielu watkach liczacych bez przerwy
/// zostaje przeglosowana — zmierzone na tej maszynie: samo obnizenie
/// priorytetu ruszylo p99 z 16,80 ms na 16,36 ms, czyli praktycznie wcale.
/// Priorytet wejscia-wyjscia dziala inaczej: menedzer wejscia-wyjscia trzyma
/// osobne kolejki wedlug priorytetu i zadania niskopriorytetowe obsluguje
/// dopiero po zwyklych.
/// </para>
/// <para>
/// Nie obiecuje to izolacji. Kolejka w kontrolerze dysku, operacje juz
/// wyslane do urzadzenia i wspoldzielone zasoby systemu plikow pozostaja
/// poza zasiegiem tego ustawienia.
/// </para>
/// </summary>
internal static partial class IoPriorityNativeMethods
{
    /// <summary>ProcessIoPriority z PROCESSINFOCLASS.</summary>
    internal const int ProcessIoPriority = 33;

    /// <summary>IO_PRIORITY_HINT: ponizej zwyklego ruchu uzytkownika.</summary>
    internal const uint IoPriorityVeryLow = 0;

    /// <summary>IO_PRIORITY_HINT: tlo, ustepuje zwyklym zadaniom.</summary>
    internal const uint IoPriorityLow = 1;

    /// <summary>IO_PRIORITY_HINT: wartosc domyslna procesu.</summary>
    internal const uint IoPriorityNormal = 2;

    internal const int StatusSuccess = 0;

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQueryInformationProcess(
        nint processHandle,
        int processInformationClass,
        out uint processInformation,
        uint processInformationLength,
        out uint returnLength);

    [LibraryImport("ntdll.dll")]
    internal static partial int NtSetInformationProcess(
        nint processHandle,
        int processInformationClass,
        in uint processInformation,
        uint processInformationLength);
}
