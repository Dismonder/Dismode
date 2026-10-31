using Dismode.UI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Dismode.UI;

internal sealed class EdgePanelHandleWindow : Window, IDisposable
{
    private readonly EdgePanelNativeWindow _native;
    internal RectInt32 HandleBounds { get; private set; }

    internal EdgePanelHandleWindow()
    {
        Title = "Dismode — uchwyt panelu";
        Border tab = new()
        {
            CornerRadius = new(4, 0, 0, 4),
            Background = (Brush)Application.Current.Resources["DismodeAccentSoftBrush"],
            BorderBrush = (Brush)Application.Current.Resources["DismodeAccentBrush"],
            BorderThickness = new(1, 0, 0, 0),
            Child = new Border
            {
                Width = 2,
                Margin = new(0, 16, 0, 16),
                CornerRadius = new(1),
                Background = (Brush)Application.Current.Resources["DismodeAccentBrush"],
            },
        };
        Content = tab;
        _native = new(this);
        Reposition();
        _native.Show();
    }

    internal void Reposition()
    {
        RectInt32 monitor = EdgePanelNativeWindow.PrimaryBounds;
        int width = (int)Math.Ceiling(6 * _native.Scale);
        int height = (int)Math.Ceiling(96 * _native.Scale);
        RectInt32 bounds = new(monitor.X + monitor.Width - width, monitor.Y + (monitor.Height - height) / 2, width, height);
        if (bounds != HandleBounds)
        {
            HandleBounds = bounds;
            _native.Position(bounds);
        }
    }

    public void Dispose()
    {
        _native.Hide();
        _native.Dispose();
        Close();
    }
}
