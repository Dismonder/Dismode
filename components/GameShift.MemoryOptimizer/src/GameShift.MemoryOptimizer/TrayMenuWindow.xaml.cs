using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using GameShift.MemoryOptimizer.Core.Models;
using GameShift.MemoryOptimizer.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace GameShift.MemoryOptimizer;

internal sealed partial class TrayMenuWindow : Window
{
    private const int BaseWidth = 400;
    private const int BaseHeight = 548;
    private const uint MonitorDefaultToNearest = 2;
    private const uint DpiDefault = 96;
    private readonly nint _windowHandle;
    private readonly Action _open;
    private readonly Func<Task> _optimize;
    private readonly Func<Task> _togglePause;
    private readonly Func<Task<bool>> _disable;
    private readonly Action _exit;
    private bool _isShown;
    private bool _disableInProgress;
    private bool _isClosed;

    internal TrayMenuWindow(
        MemoryOptimizerStatus? status,
        bool canOptimize,
        bool canPause,
        Action open,
        Func<Task> optimize,
        Func<Task> togglePause,
        Func<Task<bool>> disable,
        Action exit)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(optimize);
        ArgumentNullException.ThrowIfNull(togglePause);
        ArgumentNullException.ThrowIfNull(disable);
        ArgumentNullException.ThrowIfNull(exit);

        InitializeComponent();
        _windowHandle = WindowNative.GetWindowHandle(this);
        _open = open;
        _optimize = optimize;
        _togglePause = togglePause;
        _disable = disable;
        _exit = exit;
        Title = "GameShift Memory Optimizer";
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(
                hasBorder: false,
                hasTitleBar: false);
        }

        Activated += OnActivated;
        Closed += OnClosed;
        RemoveNonClientFrame();
        ApplyWindowChrome();
        UpdateState(status, canOptimize, canPause);
    }

    internal event EventHandler? Dismissed;

    internal void VerifyLayoutForStartupProbe()
    {
        // Measure the real XAML without activating a window or touching the desktop.
        MenuRoot.Measure(new Windows.Foundation.Size(BaseWidth, BaseHeight));
        MenuRoot.Arrange(new Windows.Foundation.Rect(0, 0, BaseWidth, BaseHeight));
        MenuRoot.UpdateLayout();
        if (MenuRoot.DesiredSize.Width > BaseWidth || MenuRoot.DesiredSize.Height > BaseHeight ||
            ActionButtonsPanel.DesiredSize.Height > ActionArea.ActualHeight + 1)
        {
            throw new InvalidOperationException("Memory Optimizer tray content exceeds its window bounds.");
        }

        VerifyNoNonClientFrame();
    }

    /// <summary>
    /// Pilnuje, ze okno menu nie ma ramki nieklienckiej.
    /// <para>
    /// Rozjazd miedzy prostokatem okna a prostokatem klienta to dokladnie ta
    /// ramka, ktora rysowala sie na bialo wokol ciemnego panelu. Sprawdzenie
    /// stoi tu, a nie w tescie jednostkowym, bo wymaga prawdziwego uchwytu
    /// okna; probe startowa uruchamia skrypt budujacy przy kazdej kompilacji.
    /// </para>
    /// </summary>
    private void VerifyNoNonClientFrame()
    {
        if (!GetWindowRect(_windowHandle, out Rect window) ||
            !GetClientRect(_windowHandle, out Rect client))
        {
            return;
        }

        int horizontal = (window.Right - window.Left) - (client.Right - client.Left);
        int vertical = (window.Bottom - window.Top) - (client.Bottom - client.Top);
        if (horizontal != 0 || vertical != 0)
        {
            throw new InvalidOperationException(
                "Okno menu w trayu ma ramke niekliencka " +
                $"({horizontal} px w poziomie, {vertical} px w pionie), " +
                "ktora rysuje sie jako biala obwodka wokol panelu.");
        }
    }

    internal void UpdateState(MemoryOptimizerStatus? status, bool canOptimize, bool canPause)
    {
        if (_isClosed)
        {
            return;
        }

        TrayMenuState state = TrayMenuState.Create(status, canOptimize, canPause, CultureInfo.CurrentCulture);
        MemoryPercentText.Text = state.PercentText;
        AvailableMemoryText.Text = state.AvailableText;
        MemoryUsageText.Text = state.UsageText;
        RamProgressBar.Value = state.Percent ?? 0;
        RamProgressBar.Opacity = state.Percent.HasValue ? 1 : 0.25;
        MemoryPressureText.Text = state.PressureText;
        MemoryStatusText.Text = state.StatusText;
        ServiceDetailText.Text = state.DetailText;
        ToolTipService.SetToolTip(ServiceDetailText, state.DetailText);
        ServiceStatusDot.Fill = (Brush)Application.Current.Resources[state.StatusBrush];
        MemoryPercentText.Foreground = (Brush)Application.Current.Resources[state.PressureBrush];
        RamProgressBar.Foreground = MemoryPercentText.Foreground;
        PauseMenuTitle.Text = state.PauseText;
        PauseMenuIcon.Glyph = status?.IsPaused == true ? "\uE768" : "\uE769";
        OptimizeMenuTitle.Text = state.IsBusy ? "Optymalizacja w toku…" : "Optymalizuj zapisany profil";
        OptimizeMenuButton.IsEnabled = state.CanOptimize;
        PauseMenuButton.IsEnabled = state.CanPause;
        OptimizeProgress.IsActive = state.IsBusy;
        OptimizeProgress.Opacity = state.IsBusy ? 1 : 0;
    }

    /// <summary>
    /// Zdejmuje niekliencka ramke okna — zrodlo bialej obwodki wokol menu.
    /// <para>
    /// <see cref="OverlappedPresenter.SetBorderAndTitleBar"/> z dwoma
    /// <c>false</c> zostawia w stylu <c>WS_DLGFRAME</c>. Zmierzone na zywym
    /// oknie menu: styl <c>0x14480000</c>, a prostokat okna byl wiekszy od
    /// prostokata klienta o 6 pikseli w kazdej osi przy 144 DPI, czyli po trzy
    /// piksele ramki z kazdej strony. Odczyt pikseli lewej krawedzi dawal
    /// <c>E3E3E3</c>, <c>FFFFFF</c>, <c>F0F0F0</c>, dopiero potem
    /// <c>273640</c> z wlasnego obramowania panelu. Zdjecie samego
    /// <c>WS_DLGFRAME</c> wystarcza; <c>WS_EX_WINDOWEDGE</c> jest na tym
    /// oknie ustawione, ale jego usuwanie nic nie zmienialo, wiec tego nie
    /// robimy.
    /// </para>
    /// <para>
    /// <c>OverlappedPresenter.CreateForContextMenu()</c> sprawdzone jako
    /// wyjscie zgodne z API: samo zmniejsza ramke z szesciu pikseli do dwoch,
    /// ale jej nie usuwa, wiec nie zastepuje tej zmiany stylu.
    /// </para>
    /// <para>
    /// Mowi to o zachowaniu zmierzonym na tym Windowsie i tej wersji Windows
    /// App SDK. Gdyby w przyszlosci pojawilo sie ponowne
    /// <c>SetPresenter</c> albo <c>SetBorderAndTitleBar</c>, trzeba wywolac
    /// te metode jeszcze raz, bo prezenter nie obiecuje konkretnych bitow
    /// stylu.
    /// </para>
    /// <para>
    /// Po zdjeciu <c>WS_DLGFRAME</c> prostokat klienta zrownal sie z
    /// prostokatem okna (600x822 pikseli przy 144 DPI), a krawedz zaczyna sie
    /// od <c>273640</c>. Dlatego atrybuty DWM tego nie naprawialy — one
    /// dotycza obramowania rysowanego przez kompozytor, a nie ramki
    /// nieklienckiej.
    /// </para>
    /// <para>
    /// Uwaga przy sprawdzaniu z zewnatrz: proces mierzacy musi byc swiadomy
    /// DPI. Dla procesu nieswiadomego Windows wirtualizuje
    /// <c>GetWindowRect</c> i <c>GetClientRect</c>, dzielac wynik przez skale,
    /// a <c>PrintWindow</c> i tak rysuje powierzchnie w pelnym rozmiarze —
    /// wychodzi z tego zrzut przyciety do lewego gornego rogu, ktory latwo
    /// wziac za blad ukladu.
    /// </para>
    /// </summary>
    private void RemoveNonClientFrame()
    {
        const int StyleIndex = -16;
        const int DialogFrame = 0x00400000;
        const uint FrameChanged = 0x0020;
        const uint NoMove = 0x0002;
        const uint NoSize = 0x0001;
        const uint NoZOrder = 0x0004;
        const uint NoActivate = 0x0010;

        int style = GetWindowLong(_windowHandle, StyleIndex);
        if ((style & DialogFrame) == 0)
        {
            return;
        }

        Marshal.SetLastSystemError(0);
        if (SetWindowLong(_windowHandle, StyleIndex, style & ~DialogFrame) == 0 &&
            Marshal.GetLastWin32Error() != 0)
        {
            // Poprzedni styl rowny zeru jest poprawnym wynikiem, dlatego
            // liczy sie dopiero para: zero i niezerowy kod bledu.
            ChromeDiagnostics = $"WS_DLGFRAME: blad {Marshal.GetLastWin32Error()}";
            Debug.WriteLine($"Tray window {ChromeDiagnostics}");
            return;
        }


        // Bez SWP_FRAMECHANGED okno nie przeliczy obszaru nieklienckiego
        // i ramka zostanie na ekranie mimo zmienionego stylu.
        _ = SetWindowPos(
            _windowHandle,
            nint.Zero,
            0,
            0,
            0,
            0,
            FrameChanged | NoMove | NoSize | NoZOrder | NoActivate);
    }

    /// <summary>
    /// Nadaje ciemny, zaokraglony wyglad ramki rysowanej przez kompozytor.
    /// <para>
    /// To inne obramowanie niz to z <see cref="RemoveNonClientFrame"/>.
    /// Kolor <c>COLOR_NONE</c> jest przyjmowany (HRESULT 0) i dotyczy
    /// cienkiej ramki DWM; bialej obwodki nie zdejmowal, bo ta byla ramka
    /// niekliencka.
    /// </para>
    /// </summary>
    private void ApplyWindowChrome()
    {
        ChromeDiagnostics = string.Empty;
        const uint ImmersiveDarkMode = 20;
        const uint CornerPreference = 33;
        const uint BorderColour = 34;
        const uint RoundedCorners = 2;
        const uint NoBorder = 0xFFFFFFFE;
        const uint DefaultBorder = uint.MaxValue;

        SetWindowAttribute(ImmersiveDarkMode, 1);
        SetWindowAttribute(CornerPreference, RoundedCorners);
        // W wysokim kontrascie ramka jest informacja, nie ozdoba — zostaje.
        SetWindowAttribute(
            BorderColour,
            new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast
                ? DefaultBorder
                : NoBorder);
    }

    /// <summary>
    /// Wynik ostatniego nadania ramki: pusty, gdy wszystko przeszlo, albo
    /// lista atrybutow z kodami bledu.
    /// <para>
    /// Wczesniej niepowodzenie szlo do Debug.WriteLine, czyli w wydaniu
    /// donikad. Ciche niepowodzenie tych wywolan bylo jedna z hipotez o biala
    /// obwodke; okazalo sie, ze wszystkie przechodza, a obwodke rysowala ramka
    /// niekliencka. Zapis zostaje, bo bez niego nie da sie odroznic
    /// przyjetego atrybutu od odrzuconego.
    /// </para>
    /// </summary>
    internal string ChromeDiagnostics { get; private set; } = string.Empty;

    private void SetWindowAttribute(uint attribute, uint value)
    {
        int result = DwmSetWindowAttribute(_windowHandle, attribute, ref value, sizeof(uint));
        if (result < 0)
        {
            string entry = $"atrybut {attribute}: HRESULT 0x{result:X8}";
            ChromeDiagnostics = ChromeDiagnostics.Length == 0
                ? entry
                : ChromeDiagnostics + "; " + entry;
            Debug.WriteLine($"Tray window {entry}");
        }
    }

    internal void ShowAtCursor()
    {
        if (_isShown)
        {
            Activate();
            return;
        }

        _isShown = true;
        PositionAtCursor();
        Activate();
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed)
            {
                return;
            }
            _ = SetForegroundWindow(_windowHandle);
            OptimizeMenuButton.Focus(FocusState.Programmatic);
        });
    }

    internal void CloseMenu()
    {
        if (!_isShown)
        {
            return;
        }

        _isShown = false;
        Close();
    }

    private void PositionAtCursor()
    {
        if (!GetCursorPos(out Point cursor))
        {
            return;
        }

        // Move the still-hidden window to the target monitor before reading its DPI.
        AppWindow.Move(new(cursor.X, cursor.Y));
        uint dpi = GetDpiForWindow(_windowHandle);
        if (dpi == 0)
        {
            dpi = DpiDefault;
        }

        int width = checked((int)Math.Round(BaseWidth * dpi / 96d));
        int height = checked((int)Math.Round(BaseHeight * dpi / 96d));
        RectInt32 workArea = GetWorkArea(cursor);
        int x = cursor.X - width + checked((int)Math.Round(12 * dpi / 96d));
        int y = cursor.Y - height - checked((int)Math.Round(8 * dpi / 96d));
        if (x < workArea.X)
        {
            x = cursor.X + checked((int)Math.Round(8 * dpi / 96d));
        }

        if (y < workArea.Y)
        {
            y = cursor.Y + checked((int)Math.Round(8 * dpi / 96d));
        }

        x = Math.Clamp(x, workArea.X, workArea.X +
            Math.Max(0, workArea.Width - width));
        y = Math.Clamp(y, workArea.Y, workArea.Y +
            Math.Max(0, workArea.Height - height));
        AppWindow.MoveAndResize(new(x, y, width, height));
    }

    private static RectInt32 GetWorkArea(Point point)
    {
        nint monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        MonitorInfo info = new()
        {
            Size = checked((uint)Marshal.SizeOf<MonitorInfo>()),
        };
        return monitor != nint.Zero && GetMonitorInfo(monitor, ref info)
            ? info.WorkArea
            : new RectInt32(0, 0, 1920, 1080);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_isShown && !_disableInProgress &&
            args.WindowActivationState == WindowActivationState.Deactivated)
        {
            CloseMenu();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _isShown = false;
        Activated -= OnActivated;
        Closed -= OnClosed;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void MenuRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && !_disableInProgress)
        {
            CloseMenu();
            e.Handled = true;
        }
    }

    private void CloseMenuButton_Click(object sender, RoutedEventArgs e) =>
        CloseMenu();

    private async void OptimizeMenuButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await _optimize().ConfigureAwait(true);
    }

    private async void PauseMenuButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await _togglePause().ConfigureAwait(true);
    }

    private void OpenMenuButton_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _open();
    }

    private void ExitMenuButton_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _exit();
    }

    private void DisableMenuButton_Click(object sender, RoutedEventArgs e)
    {
        ActionButtonsPanel.Visibility = Visibility.Collapsed;
        DisablePromptPanel.Visibility = Visibility.Visible;
        ConfirmDisableButton.Focus(FocusState.Programmatic);
    }

    private void CancelDisableButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disableInProgress)
        {
            return;
        }

        DisablePromptPanel.Visibility = Visibility.Collapsed;
        ActionButtonsPanel.Visibility = Visibility.Visible;
        DisableStatusText.Text = string.Empty;
        DisableMenuButton.Focus(FocusState.Programmatic);
    }

    private async void ConfirmDisableButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_disableInProgress)
        {
            return;
        }

        _disableInProgress = true;
        CloseMenuButton.IsEnabled = false;
        ConfirmDisableButton.IsEnabled = false;
        CancelDisableButton.IsEnabled = false;
        DisableStatusText.Text =
            "Oczekiwanie na potwierdzenie UAC i zatrzymanie usługi…";
        try
        {
            if (await _disable().ConfigureAwait(true))
            {
                CloseMenu();
            }
            else
            {
                DisableStatusText.Text =
                    "Nie wyłączono komponentu. Usługa nadal działa.";
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or
                TimeoutException or OperationCanceledException or
                Win32Exception)
        {
            DisableStatusText.Text = exception.Message;
        }
        finally
        {
            _disableInProgress = false;
            if (!_isClosed)
            {
                CloseMenuButton.IsEnabled = true;
                ConfirmDisableButton.IsEnabled = true;
                CancelDisableButton.IsEnabled = true;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal RectInt32 MonitorArea;
        internal RectInt32 WorkArea;
        internal uint Flags;
    }

#pragma warning disable SYSLIB1054
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref uint value, int size);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    private static extern nint MonitorFromPoint(
        Point point,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        nint monitor,
        ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(
        nint window,
        int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(
        nint window,
        int index,
        int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        nint window,
        out Rect rectangle);

    [DllImport("user32.dll", EntryPoint = "GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(
        nint window,
        out Rect rectangle);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
#pragma warning restore SYSLIB1054
}
