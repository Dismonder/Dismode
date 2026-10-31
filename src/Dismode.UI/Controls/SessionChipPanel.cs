using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Dismode.UI.Controls;

public sealed class SessionChipPanel : Panel
{
    private const double Spacing = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }

        return Layout(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Layout(double width, bool arrange)
    {
        double x = 0;
        double y = 0;
        double rowHeight = 0;
        double usedWidth = 0;
        foreach (UIElement child in Children)
        {
            Size size = child.DesiredSize;
            if (x > 0 && x + size.Width > width)
            {
                x = 0;
                y += rowHeight + Spacing;
                rowHeight = 0;
            }

            if (arrange)
            {
                child.Arrange(new Rect(x, y, size.Width, size.Height));
            }

            usedWidth = Math.Max(usedWidth, x + size.Width);
            x += size.Width + Spacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return new Size(usedWidth, y + rowHeight);
    }
}
