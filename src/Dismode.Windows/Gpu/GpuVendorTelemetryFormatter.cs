using System.Globalization;

namespace Dismode.Windows.Gpu;

/// <summary>Sklada tekst nakladki z odczytow producenta; brakujace wartosci nie tworza segmentu.</summary>
public static class GpuVendorTelemetryFormatter
{
    public static string? Format(GpuVendorTelemetrySnapshot? snapshot)
    {
        if (snapshot is not { } value)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[96];
        int length = 0;
        Append(buffer, ref length, value.TemperatureCelsius, "0", " °C");
        Append(buffer, ref length, value.CoreClockMHz, "0", " MHz");
        Append(buffer, ref length, value.BoardPowerWatts, "0", " W");
        Append(buffer, ref length, value.FanPercent, "0", "% went.");
        return length == 0 ? null : new string(buffer[..length]);
    }

    private static void Append(Span<char> buffer, ref int length, double? number, string format, string suffix)
    {
        if (number is not double current || !double.IsFinite(current))
        {
            return;
        }

        string prefix = length == 0 ? "GPU " : " | ";
        if (!prefix.TryCopyTo(buffer[length..]))
        {
            return;
        }

        int start = length;
        length += prefix.Length;
        if (current.TryFormat(buffer[length..], out int written, format, CultureInfo.CurrentCulture)
            && suffix.TryCopyTo(buffer[(length + written)..]))
        {
            length += written + suffix.Length;
            return;
        }

        length = start;
    }
}
