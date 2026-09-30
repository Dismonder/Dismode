using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Dismode.UI.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = WmApp + 0x42;
    private const uint OpenCommand = 1001;
    private const uint ExitCommand = 1002;
    private const uint AutomaticOptimizationCommand = 1003;

    private readonly nint _windowHandle;
    private readonly nint _iconHandle;
    private readonly WindowSubclassProcedure _subclassProcedure;
    private readonly uint _taskbarCreatedMessage;
    private NotifyIconData _iconData;
    private bool _automaticOptimizationEnabled;
    private string? _activeGameDisplayName;
    private bool _disposed;

    public TrayIconService(nint windowHandle, string iconPath)
    {
        if (windowHandle == nint.Zero)
        {
            throw new ArgumentException(
                "Uchwyt okna Dismode jest nieprawidłowy.",
                nameof(windowHandle));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(iconPath);
        if (!File.Exists(iconPath))
        {
            throw new FileNotFoundException(
                "Nie znaleziono ikony Dismode.",
                iconPath);
        }

        _windowHandle = windowHandle;
        // Po restarcie Eksploratora pasek zadan powstaje od nowa i rozglasza
        // TaskbarCreated; bez ponownego dodania ikona znika, a schowane okno
        // traci jedyne wejscie. Filtr komunikatow: rozgloszenie z powloki ma
        // dojsc takze wtedy, gdy ktos uruchomi UI jako administrator.
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreatedMessage != 0)
        {
            _ = ChangeWindowMessageFilterEx(
                _windowHandle,
                _taskbarCreatedMessage,
                MessageFilterAllow,
                nint.Zero);
        }

        _iconHandle = LoadImage(
            nint.Zero,
            iconPath,
            ImageIcon,
            0,
            0,
            LoadFromFile | LoadDefaultSize);
        if (_iconHandle == nint.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows nie wczytał ikony zasobnika Dismode.");
        }

        _subclassProcedure = WindowSubclassCallback;
        if (!SetWindowSubclass(
                _windowHandle,
                _subclassProcedure,
                IconId,
                nint.Zero))
        {
            _ = DestroyIcon(_iconHandle);
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows nie podłączył obsługi ikony zasobnika.");
        }

        _iconData = CreateIconData(
            NotifyIconMessage | NotifyIconIcon | NotifyIconTip
                | NotifyIconShowTip);
        if (!ShellNotifyIcon(NotifyIconAdd, ref _iconData))
        {
            if (!ShellNotifyIcon(NotifyIconModify, ref _iconData))
            {
                _ = RemoveWindowSubclass(
                    _windowHandle,
                    _subclassProcedure,
                    IconId);
                _ = DestroyIcon(_iconHandle);
                _disposed = true;
                return;
            }
        }

        _iconData.TimeoutOrVersion = NotifyIconVersion4;
        _ = ShellNotifyIcon(NotifyIconSetVersion, ref _iconData);
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    /// <summary>
    /// Uzytkownik kliknal w menu ikony pozycje automatycznej optymalizacji.
    /// Ikona nie zmienia stanu sama: okno jest jego wlascicielem, zapisuje
    /// go i oddaje przez <see cref="SetAutomaticOptimizationState"/>.
    /// </summary>
    public event EventHandler? AutomaticOptimizationToggleRequested;

    /// <summary>
    /// Odswieza znacznik przy pozycji menu i podpowiedz ikony, zeby po
    /// najechaniu bylo widac, czy Dismode czuwa nad wykrytymi grami.
    /// </summary>
    public void SetAutomaticOptimizationState(bool enabled)
    {
        _automaticOptimizationEnabled = enabled;
        RefreshTooltip();
    }

    /// <summary>
    /// Gra z aktywna sesja albo null, gdy sesji nie ma. Podpowiedz ikony
    /// mowi wtedy, co Dismode wlasnie pilnuje — wzorzec z narzedzi
    /// siedzacych w zasobniku (HandheldCompanion pokazuje tak biezacy
    /// profil), bo okno jest schowane, a najechanie na ikone to najtanszy
    /// sposob, by to sprawdzic.
    /// </summary>
    public void SetActiveGame(string? gameDisplayName)
    {
        string? normalized = string.IsNullOrWhiteSpace(gameDisplayName)
            ? null
            : gameDisplayName.Trim();
        if (string.Equals(_activeGameDisplayName, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _activeGameDisplayName = normalized;
        RefreshTooltip();
    }

    private void RefreshTooltip()
    {
        if (_disposed)
        {
            return;
        }

        _iconData.Tip = Truncate(
            DescribeTooltip(
                _automaticOptimizationEnabled,
                _activeGameDisplayName),
            127);
        NotifyIconData tooltip = _iconData;
        tooltip.Flags = NotifyIconTip | NotifyIconShowTip;
        _ = ShellNotifyIcon(NotifyIconModify, ref tooltip);
    }

    public void ShowNotification(string title, string message)
    {
        if (_disposed)
        {
            return;
        }

        NotifyIconData notification = _iconData;
        notification.Flags = NotifyIconInfo;
        notification.InfoTitle = Truncate(title, 63);
        notification.Info = Truncate(message, 255);
        notification.InfoFlags = NotifyInfoInformation;
        notification.TimeoutOrVersion = 4000;
        _ = ShellNotifyIcon(NotifyIconModify, ref notification);
    }

    private nint WindowSubclassCallback(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nint referenceData)
    {
        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            RestoreIcon();
        }
        else if (message == CallbackMessage)
        {
            uint mouseMessage = unchecked((uint)(long)lParam) & 0xFFFF;
            if (mouseMessage == WmLeftButtonDoubleClick)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
                return nint.Zero;
            }

            if (mouseMessage is WmContextMenu or WmRightButtonUp)
            {
                ShowContextMenu();
                return nint.Zero;
            }
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    /// <summary>
    /// Nowy pasek zadan nie zna naszej ikony: dodaje ja od nowa z biezaca
    /// podpowiedzia i wersja zachowania, tak jak przy starcie.
    /// </summary>
    private void RestoreIcon()
    {
        if (_disposed)
        {
            return;
        }

        _iconData.Tip = Truncate(
            DescribeTooltip(
                _automaticOptimizationEnabled,
                _activeGameDisplayName),
            127);
        if (!ShellNotifyIcon(NotifyIconAdd, ref _iconData))
        {
            _ = ShellNotifyIcon(NotifyIconModify, ref _iconData);
        }

        _iconData.TimeoutOrVersion = NotifyIconVersion4;
        _ = ShellNotifyIcon(NotifyIconSetVersion, ref _iconData);
    }

    private void ShowContextMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == nint.Zero)
        {
            return;
        }

        try
        {
            _ = AppendMenu(
                menu,
                MenuString,
                OpenCommand,
                "Otwórz Dismode");
            _ = AppendMenu(
                menu,
                MenuSeparator,
                0,
                string.Empty);
            _ = AppendMenu(
                menu,
                MenuString
                    | (_automaticOptimizationEnabled
                        ? MenuChecked
                        : MenuUnchecked),
                AutomaticOptimizationCommand,
                "Automatycznie optymalizuj wykryte gry");
            _ = AppendMenu(
                menu,
                MenuSeparator,
                0,
                string.Empty);
            _ = AppendMenu(
                menu,
                MenuString,
                ExitCommand,
                "Wyłącz Dismode — Memory Optimizer pozostaje");
            if (!GetCursorPos(out Point cursor))
            {
                return;
            }

            _ = SetForegroundWindow(_windowHandle);
            uint command = TrackPopupMenu(
                menu,
                TrackReturnCommand | TrackNonotify | TrackRightButton,
                cursor.X,
                cursor.Y,
                0,
                _windowHandle,
                nint.Zero);
            _ = PostMessage(
                _windowHandle,
                WmNull,
                nuint.Zero,
                nint.Zero);
            if (command == OpenCommand)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (command == AutomaticOptimizationCommand)
            {
                AutomaticOptimizationToggleRequested?.Invoke(
                    this,
                    EventArgs.Empty);
            }
            else if (command == ExitCommand)
            {
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private NotifyIconData CreateIconData(uint flags) => new()
    {
        Size = checked((uint)Marshal.SizeOf<NotifyIconData>()),
        WindowHandle = _windowHandle,
        Id = IconId,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        IconHandle = _iconHandle,
        Tip = DescribeTooltip(
            _automaticOptimizationEnabled,
            _activeGameDisplayName),
        Info = string.Empty,
        InfoTitle = string.Empty,
        BalloonIconHandle = nint.Zero,
    };

    private static string DescribeTooltip(
        bool automaticOptimizationEnabled,
        string? activeGameDisplayName)
    {
        if (activeGameDisplayName is not null)
        {
            return $"Dismode — sesja: {activeGameDisplayName}";
        }

        return automaticOptimizationEnabled
            ? "Dismode — czuwa: wykryta gra dostanie sesję automatycznie"
            : "Dismode — działa w tle";
    }

    private static string Truncate(string value, int maximumLength)
    {
        string normalized = string.IsNullOrWhiteSpace(value)
            ? "Dismode"
            : value.ReplaceLineEndings(" ").Trim();
        return normalized[..Math.Min(normalized.Length, maximumLength)];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _ = ShellNotifyIcon(NotifyIconDelete, ref _iconData);
        _ = RemoveWindowSubclass(
            _windowHandle,
            _subclassProcedure,
            IconId);
        _ = DestroyIcon(_iconHandle);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowSubclassProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nint referenceData);

#pragma warning disable SYSLIB1054
    [DllImport(
        "shell32.dll",
        EntryPoint = "Shell_NotifyIconW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(
        uint message,
        ref NotifyIconData data);

    [DllImport(
        "user32.dll",
        EntryPoint = "LoadImageW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern nint LoadImage(
        nint instance,
        string name,
        uint type,
        int desiredWidth,
        int desiredHeight,
        uint loadFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint windowHandle,
        WindowSubclassProcedure subclassProcedure,
        nuint subclassId,
        nint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint windowHandle,
        WindowSubclassProcedure subclassProcedure,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport(
        "user32.dll",
        EntryPoint = "RegisterWindowMessageW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(
        nint windowHandle,
        uint message,
        uint action,
        nint changeFilterStruct);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport(
        "user32.dll",
        EntryPoint = "AppendMenuW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(
        nint menu,
        uint flags,
        nuint itemId,
        string text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint windowHandle,
        nint rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);
#pragma warning restore SYSLIB1054

    private const uint WmNull = 0x0000;
    private const uint WmRightButtonUp = 0x0205;
    private const uint WmLeftButtonDoubleClick = 0x0203;
    private const uint WmContextMenu = 0x007B;
    private const uint WmApp = 0x8000;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x0010;
    private const uint LoadDefaultSize = 0x0040;
    private const uint NotifyIconAdd = 0x00000000;
    private const uint NotifyIconModify = 0x00000001;
    private const uint NotifyIconDelete = 0x00000002;
    private const uint NotifyIconSetVersion = 0x00000004;
    private const uint NotifyIconMessage = 0x00000001;
    private const uint NotifyIconIcon = 0x00000002;
    private const uint NotifyIconTip = 0x00000004;
    private const uint NotifyIconInfo = 0x00000010;
    private const uint NotifyIconShowTip = 0x00000080;
    private const uint NotifyInfoInformation = 0x00000001;
    private const uint NotifyIconVersion4 = 4;
    private const uint MessageFilterAllow = 1;
    private const uint MenuString = 0x00000000;
    private const uint MenuUnchecked = 0x00000000;
    private const uint MenuChecked = 0x00000008;
    private const uint MenuSeparator = 0x00000800;
    private const uint TrackRightButton = 0x0002;
    private const uint TrackNonotify = 0x0080;
    private const uint TrackReturnCommand = 0x0100;
}
