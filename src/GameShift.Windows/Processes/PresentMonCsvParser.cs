using System.Globalization;
using System.Text;

namespace GameShift.Windows.Processes;

internal sealed class PresentMonCsvParser
{
    private int _processIdIndex = -1;
    private int _swapChainIndex = -1;
    private int _frameTimeIndex = -1;

    internal bool HasValidHeader =>
        _processIdIndex >= 0
        && _swapChainIndex >= 0
        && _frameTimeIndex >= 0;

    internal bool TryParse(
        string line,
        out PresentMonFrame frame)
    {
        frame = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string[] fields = SplitCsv(line);
        if (!HasValidHeader)
        {
            ReadHeader(fields);
            return false;
        }

        int highestIndex = Math.Max(
            _processIdIndex,
            Math.Max(_swapChainIndex, _frameTimeIndex));
        if (fields.Length <= highestIndex
            || !int.TryParse(
                fields[_processIdIndex],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId)
            || processId <= 0
            || string.IsNullOrWhiteSpace(fields[_swapChainIndex])
            || !double.TryParse(
                fields[_frameTimeIndex],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double frameTimeMilliseconds)
            || !double.IsFinite(frameTimeMilliseconds)
            || frameTimeMilliseconds is <= 0 or > 10_000)
        {
            return false;
        }

        frame = new(
            processId,
            fields[_swapChainIndex],
            frameTimeMilliseconds);
        return true;
    }

    internal static string[] SplitCsv(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        List<string> fields = [];
        StringBuilder current = new();
        bool insideQuotes = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (character == '"')
            {
                if (insideQuotes
                    && index + 1 < line.Length
                    && line[index + 1] == '"')
                {
                    _ = current.Append('"');
                    index++;
                }
                else
                {
                    insideQuotes = !insideQuotes;
                }

                continue;
            }

            if (character == ',' && !insideQuotes)
            {
                fields.Add(current.ToString());
                _ = current.Clear();
                continue;
            }

            _ = current.Append(character);
        }

        fields.Add(current.ToString());
        if (fields.Count > 0)
        {
            fields[0] = fields[0].TrimStart('\uFEFF');
        }

        return [.. fields];
    }

    private void ReadHeader(IReadOnlyList<string> fields)
    {
        _processIdIndex = FindColumn(fields, "ProcessID");
        _swapChainIndex = FindColumn(fields, "SwapChainAddress");
        _frameTimeIndex = FindColumn(fields, "FrameTime");
        if (!HasValidHeader)
        {
            _processIdIndex = -1;
            _swapChainIndex = -1;
            _frameTimeIndex = -1;
        }
    }

    private static int FindColumn(
        IReadOnlyList<string> fields,
        string name)
    {
        for (int index = 0; index < fields.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(fields[index], name))
            {
                return index;
            }
        }

        return -1;
    }
}

internal readonly record struct PresentMonFrame(
    int ProcessId,
    string SwapChainAddress,
    double FrameTimeMilliseconds);
