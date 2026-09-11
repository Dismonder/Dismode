using System.Globalization;
using GameShift.MemoryOptimizer.Core.Models;

namespace GameShift.MemoryOptimizer.Presentation;

internal static class MemoryUiFormatting
{
    public static string FormatBytes(ulong bytes, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return bytes >= 1024UL * 1024 * 1024
            ? (bytes / (1024d * 1024 * 1024)).ToString("0.0", culture) + " GB"
            : (bytes / (1024d * 1024)).ToString("0", culture) + " MB";
    }

    public static string FormatCommit(MemorySnapshot snapshot, IFormatProvider? culture = null) =>
        snapshot.CommitLimitBytes == 0
            ? "Brak danych"
            : $"{FormatBytes(snapshot.CommittedBytes, culture)} / {FormatBytes(snapshot.CommitLimitBytes, culture)}";

    public static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>
    /// Tlumaczy komunikat straznika gry na jezyk interfejsu.
    /// <para>
    /// Warstwa Core pisze po angielsku i tak trafia to do dziennika. Interfejs
    /// jest polski, wiec bez tego na ekranie ladowalo zdanie w obcym jezyku
    /// posrodku polskiego zdania — widac to bylo na przegladzie pamieci i w
    /// menu w trayu.
    /// </para>
    /// <para>
    /// Kod powodu z <see cref="GuardDecision"/> nie dociera tutaj, bo
    /// <see cref="OptimizationResult"/> niesie sam komunikat. Dopasowanie idzie
    /// wiec po tresci. Zeby to nie zgnilo po cichu, test przepuszcza kazda
    /// galaz straznika przez ta metode i sprawdza, ze zadna nie wraca po
    /// angielsku.
    /// </para>
    /// </summary>
    public static string DescribeBlockReason(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        const string aktywnaSesja = "Optimization blocked while ";
        const string znanaGra = "Optimization blocked by known game process ";
        const string chroniony = "Optimization blocked by protected launcher or anti-cheat ";
        const string pelnyEkran = "Optimization blocked by unknown full-screen process ";

        if (message.StartsWith(aktywnaSesja, StringComparison.Ordinal) &&
            message.EndsWith(" is active.", StringComparison.Ordinal))
        {
            string nazwa = message[aktywnaSesja.Length..^" is active.".Length];
            return $"trwa sesja gry {nazwa}";
        }

        if (TryOpisz(message, znanaGra, out string proces))
        {
            return $"wykryto znaną grę {proces}";
        }

        if (TryOpisz(message, chroniony, out proces))
        {
            return $"działa chroniony launcher lub anty-cheat {proces}";
        }

        if (TryOpisz(message, pelnyEkran, out proces))
        {
            return $"nieznany program {proces} działa na pełnym ekranie";
        }

        return message;
    }

    private static bool TryOpisz(string message, string prefiks, out string proces)
    {
        if (message.StartsWith(prefiks, StringComparison.Ordinal) &&
            message.EndsWith('.'))
        {
            proces = message[prefiks.Length..^1];
            return proces.Length > 0;
        }

        proces = string.Empty;
        return false;
    }
}
