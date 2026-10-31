using System.Globalization;
using Dismode.Contracts.SystemOptimization;

namespace Dismode.Windows.Processes;

internal static class PresentMonCaptureLossParser
{
    internal static bool IsWarning(ReadOnlySpan<char> line) =>
        line.TrimStart().StartsWith("warning:", StringComparison.OrdinalIgnoreCase);

    internal static BenchmarkCaptureLoss Observe(BenchmarkCaptureLoss previous, string line)
    {
        ReadOnlySpan<char> text = line.AsSpan().Trim();
        if (!IsWarning(text))
        {
            return previous;
        }

        ReadOnlySpan<char> payload = text["warning:".Length..].TrimStart();
        const string eventsSuffix = " ETW events were lost.";
        const string buffersSuffix = " ETW buffers were lost.";
        bool events = payload.EndsWith(eventsSuffix, StringComparison.Ordinal);
        bool buffers = payload.EndsWith(buffersSuffix, StringComparison.Ordinal);
        if ((events || buffers)
            && uint.TryParse(payload[..^(events ? eventsSuffix.Length : buffersSuffix.Length)],
                NumberStyles.None, CultureInfo.InvariantCulture, out uint count))
        {
            // Liczniki podsumowania sa osobnymi jednostkami. Powtorzona linia
            // nie podwaja straty; czysta linia nigdy nie usuwa wczesniejszej.
            long lostEvents = Math.Max(previous.LostEvents, events ? count : 0);
            long lostBuffers = Math.Max(previous.LostBuffers, buffers ? count : 0);
            return new(previous.HasConfirmedLoss || lostEvents > 0 || lostBuffers > 0
                    ? BenchmarkCaptureLossState.Confirmed : previous.State,
                lostEvents, lostBuffers);
        }

        return previous.HasConfirmedLoss
            ? previous
            : previous with { State = BenchmarkCaptureLossState.Unknown };
    }
}
