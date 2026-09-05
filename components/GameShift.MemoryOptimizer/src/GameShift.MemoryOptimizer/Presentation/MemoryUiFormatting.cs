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
}
