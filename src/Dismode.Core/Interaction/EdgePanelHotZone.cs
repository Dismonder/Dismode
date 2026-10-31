namespace Dismode.Core.Interaction;

public readonly record struct EdgePanelRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;

    public EdgePanelRect Inflate(int by) => new(X - by, Y - by, Width + (2 * by), Height + (2 * by));
}

public static class EdgePanelHotZone
{
    private const int StripWidth = 2;

    // Pasek przy prawej krawedzi od 20 do 80 % wysokosci monitora: kursor dosuniety do
    // krawedzi trafia w niego na dowolnej wysokosci, a rogi zostaja wolne dla przycisku
    // zamykania okien i obszaru powiadomien.
    public static EdgePanelRect EdgeStrip(EdgePanelRect monitor)
    {
        int top = monitor.Y + (int)Math.Round(monitor.Height * 0.2);
        int bottom = monitor.Y + (int)Math.Round(monitor.Height * 0.8);
        return new(monitor.X + monitor.Width - StripWidth, top, StripWidth, bottom - top);
    }
}
