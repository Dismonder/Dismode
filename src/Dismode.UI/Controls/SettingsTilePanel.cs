using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Dismode.UI.Controls;

public sealed class SettingsTilePanel : Panel
{
    public double MinimumItemWidth { get; set; } = 336;

    public int MaximumColumns { get; set; } = 3;

    private int Columns(double width) => Math.Max(1,
        Math.Min(MaximumColumns, (int)((width + 16) / (MinimumItemWidth + 16))));

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width)
            ? MinimumItemWidth : availableSize.Width;
        int columns = Columns(width);
        double itemWidth = Math.Max(0, (width - (columns - 1) * 16) / columns);
        double height = 0;
        for (int row = 0; row < Children.Count; row += columns)
        {
            double rowHeight = 0;
            for (int i = row; i < Math.Min(row + columns, Children.Count); i++)
            {
                Children[i].Measure(new Size(itemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            }

            height += rowHeight + (row == 0 ? 0 : 16);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = Columns(finalSize.Width);
        double itemWidth = Math.Max(0, (finalSize.Width - (columns - 1) * 16) / columns);
        double y = 0;
        for (int row = 0; row < Children.Count; row += columns)
        {
            double rowHeight = 0;
            for (int i = row; i < Math.Min(row + columns, Children.Count); i++)
            {
                rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            }

            for (int i = row; i < Math.Min(row + columns, Children.Count); i++)
            {
                Children[i].Arrange(new Rect((i - row) * (itemWidth + 16), y, itemWidth, rowHeight));
            }

            y += rowHeight + 16;
        }

        return finalSize;
    }
}
