using System.Globalization;
using System.Runtime.InteropServices;
using GameShift.MemoryOptimizer.Core.Models;
using Microsoft.UI.Dispatching;

namespace GameShift.MemoryOptimizer;

internal sealed class TrayIconService : IDisposable
{
    private const uint WindowMessageTray = 0x8001;
    private const uint WindowMessageHotkey = 0x0312;
    private const uint WindowMessageLeftDoubleClick = 0x0203;
    private const uint WindowMessageMiddleUp = 0x0208;
    private const uint WindowMessageRightUp = 0x0205;
    private const int WindowLongWindowProcedure = -4;
    private const uint NotifyAdd = 0;
    private const uint NotifyModify = 1;
    private const uint NotifyDelete = 2;
    private const uint NotifyMessage = 1;
    private const uint NotifyIcon = 2;
    private const uint NotifyTip = 4;
    private const uint NotifyInfo = 16;
    private const uint NotifyInfoFlag = 1;
    private const int HotkeyId = 0x4753;

    private readonly nint _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _open;
    private readonly Action _showContextMenu;
    private readonly Action _optimize;
    private readonly WindowProcedure _windowProcedure;
    private readonly nint _previousWindowProcedure;
    private readonly uint _taskbarCreatedMessage;
    private NotifyIconData _data;
    private nint _ownedIcon;
    private uint _memoryPercent;
    private bool _isPaused;
    private HotkeyBinding? _hotkey;
    private bool _disposed;

    internal TrayIconService(
        nint window,
        DispatcherQueue dispatcher,
        Action open,
        Action showContextMenu,
        Action optimize)
    {
        _window = window;
        _dispatcher = dispatcher;
        _open = open;
        _showContextMenu = showContextMenu;
        _optimize = optimize;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _windowProcedure = WindowProc;
        nint callback = Marshal.GetFunctionPointerForDelegate(_windowProcedure);
        _previousWindowProcedure = SetWindowLongPtr(
            window,
            WindowLongWindowProcedure,
            callback);
        _data = CreateData(memoryPercent: 0, isPaused: false);
        _ownedIcon = _data.Icon;
        if (!ShellNotifyIcon(NotifyAdd, ref _data))
        {
            _ = DestroyIcon(_ownedIcon);
            _ownedIcon = nint.Zero;
            _ = SetWindowLongPtr(
                window,
                WindowLongWindowProcedure,
                _previousWindowProcedure);
            throw new InvalidOperationException("Cannot create tray icon.");
        }

        _ = UpdateHotkey("Ctrl+Shift+M");
    }

    internal void UpdateMemory(uint memoryPercent, bool isPaused)
    {
        memoryPercent = Math.Min(memoryPercent, 100);
        if (_disposed ||
            (_memoryPercent == memoryPercent && _isPaused == isPaused))
        {
            return;
        }

        NotifyIconData next = CreateData(memoryPercent, isPaused);
        if (ShellNotifyIcon(NotifyModify, ref next) ||
            ShellNotifyIcon(NotifyAdd, ref next))
        {
            nint previousIcon = _ownedIcon;
            _memoryPercent = memoryPercent;
            _isPaused = isPaused;
            _data = next;
            _ownedIcon = next.Icon;
            if (previousIcon != nint.Zero)
            {
                _ = DestroyIcon(previousIcon);
            }
        }
        else if (next.Icon != nint.Zero)
        {
            _ = DestroyIcon(next.Icon);
        }
    }

    internal bool UpdateHotkey(string hotkey)
    {
        if (_disposed || !HotkeyBinding.TryParse(
                hotkey,
                out HotkeyBinding requested))
        {
            return false;
        }

        if (_hotkey is { } current && current == requested)
        {
            return true;
        }

        HotkeyBinding? previous = _hotkey;
        if (previous is not null)
        {
            _ = UnregisterHotKey(_window, HotkeyId);
            _hotkey = null;
        }

        if (RegisterHotKey(
                _window,
                HotkeyId,
                (uint)(requested.Modifiers | HotkeyModifiers.NoRepeat),
                checked((int)requested.VirtualKey)))
        {
            _hotkey = requested;
            return true;
        }

        if (previous is { } fallback && RegisterHotKey(
                _window,
                HotkeyId,
                (uint)(fallback.Modifiers | HotkeyModifiers.NoRepeat),
                checked((int)fallback.VirtualKey)))
        {
            _hotkey = fallback;
        }

        return false;
    }

    internal void ShowNotification(string title, string message)
    {
        if (_disposed)
        {
            return;
        }

        NotifyIconData notification = _data;
        notification.Flags = NotifyInfo;
        notification.InfoTitle = title.Length > 63
            ? title[..63]
            : title;
        notification.Info = message.Length > 255
            ? message[..255]
            : message;
        notification.InfoFlags = NotifyInfoFlag;
        _ = ShellNotifyIcon(NotifyModify, ref notification);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = ShellNotifyIcon(NotifyDelete, ref _data);
        if (_ownedIcon != nint.Zero)
        {
            _ = DestroyIcon(_ownedIcon);
            _ownedIcon = nint.Zero;
        }

        if (_hotkey is not null)
        {
            _ = UnregisterHotKey(_window, HotkeyId);
            _hotkey = null;
        }
        _ = SetWindowLongPtr(
            _window,
            WindowLongWindowProcedure,
            _previousWindowProcedure);
    }

    private nint WindowProc(
        nint window,
        uint message,
        nint wParam,
        nint lParam)
    {
        if (message == WindowMessageTray)
        {
            switch (unchecked((uint)lParam.ToInt64()))
            {
                case WindowMessageLeftDoubleClick:
                    Enqueue(_open);
                    return nint.Zero;
                case WindowMessageMiddleUp:
                    Enqueue(_optimize);
                    return nint.Zero;
                case WindowMessageRightUp:
                    Enqueue(_showContextMenu);
                    return nint.Zero;
            }
        }
        else if (message == WindowMessageHotkey &&
            wParam.ToInt32() == HotkeyId)
        {
            Enqueue(_optimize);
            return nint.Zero;
        }
        else if (_taskbarCreatedMessage != 0 &&
            message == _taskbarCreatedMessage)
        {
            RestoreAfterExplorerRestart();
            return nint.Zero;
        }

        return CallWindowProc(
            _previousWindowProcedure,
            window,
            message,
            wParam,
            lParam);
    }

    private void RestoreAfterExplorerRestart()
    {
        if (!_disposed)
        {
            NotifyIconData restored = _data;
            _ = ShellNotifyIcon(NotifyAdd, ref restored);
        }
    }

    private void Enqueue(Action action) =>
        _ = _dispatcher.TryEnqueue(() => action());

    private NotifyIconData CreateData(uint memoryPercent, bool isPaused)
    {
        return new()
        {
            Size = checked((uint)Marshal.SizeOf<NotifyIconData>()),
            Window = _window,
            Id = 1,
            Flags = NotifyMessage | NotifyIcon | NotifyTip,
            CallbackMessage = WindowMessageTray,
            Icon = CreateMemoryIcon(memoryPercent, isPaused),
            Tip = isPaused
                ? $"GameShift Memory Optimizer — RAM {memoryPercent}% — wstrzymany"
                : $"GameShift Memory Optimizer — RAM {memoryPercent}%",
            Info = string.Empty,
            InfoTitle = string.Empty,
        };
    }

    private static nint CreateMemoryIcon(uint memoryPercent, bool isPaused)
    {
        const int size = 32;
        int[] pixels = new int[size * size];
        byte[] mask = new byte[size * size / 8];
        int background = unchecked((int)(isPaused
            ? 0xFF64748BU
            : memoryPercent >= 90
                ? 0xFFEF4444U
                : memoryPercent >= 80
                    ? 0xFFF59E0BU
                    : 0xFF00A98FU));
        int border = unchecked((int)0xFF071112U);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool outer = IsInsideRoundedSquare(x, y, inset: 1, radius: 6);
                if (!outer)
                {
                    int maskIndex = y * (size / 8) + x / 8;
                    mask[maskIndex] |= checked((byte)(0x80 >> (x % 8)));
                    continue;
                }

                pixels[y * size + x] = IsInsideRoundedSquare(
                    x,
                    y,
                    inset: 3,
                    radius: 4)
                        ? background
                        : border;
            }
        }

        DrawPercentage(
            pixels,
            size,
            memoryPercent.ToString(CultureInfo.InvariantCulture));

        nint screen = GetDC(nint.Zero);
        nint colorBitmap = nint.Zero;
        nint maskBitmap = nint.Zero;
        try
        {
            BitmapInfo bitmapInfo = new()
            {
                Header = new()
                {
                    Size = checked((uint)Marshal.SizeOf<BitmapInfoHeader>()),
                    Width = size,
                    Height = -size,
                    Planes = 1,
                    BitCount = 32,
                    SizeImage = size * size * sizeof(int),
                },
            };
            colorBitmap = CreateDIBSection(
                screen,
                ref bitmapInfo,
                0,
                out nint pixelBuffer,
                nint.Zero,
                0);
            if (colorBitmap == nint.Zero || pixelBuffer == nint.Zero)
            {
                return CreateFallbackIcon(memoryPercent, isPaused);
            }

            Marshal.Copy(pixels, 0, pixelBuffer, pixels.Length);
            maskBitmap = CreateBitmap(size, size, 1, 1, mask);
            if (maskBitmap == nint.Zero)
            {
                return CreateFallbackIcon(memoryPercent, isPaused);
            }

            IconInfo iconInfo = new()
            {
                IsIcon = true,
                ColorBitmap = colorBitmap,
                MaskBitmap = maskBitmap,
            };
            nint icon = CreateIconIndirect(ref iconInfo);
            return icon != nint.Zero
                ? icon
                : CreateFallbackIcon(memoryPercent, isPaused);
        }
        finally
        {
            if (screen != nint.Zero)
            {
                _ = ReleaseDC(nint.Zero, screen);
            }

            if (colorBitmap != nint.Zero)
            {
                _ = DeleteObject(colorBitmap);
            }

            if (maskBitmap != nint.Zero)
            {
                _ = DeleteObject(maskBitmap);
            }
        }
    }

    private static nint CreateFallbackIcon(uint memoryPercent, bool isPaused)
    {
        int iconId = isPaused
            ? 32515
            : memoryPercent >= 90
                ? 32513
                : memoryPercent >= 80
                    ? 32515
                    : 32516;
        nint icon = CopyIcon(LoadIcon(nint.Zero, new(iconId)));
        return icon != nint.Zero
            ? icon
            : throw new InvalidOperationException("Cannot create a tray icon.");
    }

    private static bool IsInsideRoundedSquare(
        int x,
        int y,
        int inset,
        int radius)
    {
        const int maximum = 31;
        int far = maximum - inset;
        if (x < inset || y < inset || x > far || y > far)
        {
            return false;
        }

        if ((x >= inset + radius && x <= far - radius) ||
            (y >= inset + radius && y <= far - radius))
        {
            return true;
        }

        int centerX = x < inset + radius
            ? inset + radius
            : far - radius;
        int centerY = y < inset + radius
            ? inset + radius
            : far - radius;
        int deltaX = x - centerX;
        int deltaY = y - centerY;
        return deltaX * deltaX + deltaY * deltaY <= radius * radius;
    }

    private static void DrawPercentage(
        int[] pixels,
        int canvasSize,
        string text)
    {
        int scale = text.Length >= 3 ? 2 : 3;
        int width = (text.Length * 3 + text.Length - 1) * scale;
        int height = 5 * scale;
        int startX = (canvasSize - width) / 2;
        int startY = (canvasSize - height) / 2;
        for (int index = 0; index < text.Length; index++)
        {
            int pattern = GetDigitPattern(text[index]);
            for (int row = 0; row < 5; row++)
            {
                int rowBits = pattern >> ((4 - row) * 3) & 0b111;
                for (int column = 0; column < 3; column++)
                {
                    if ((rowBits & 1 << (2 - column)) == 0)
                    {
                        continue;
                    }

                    int left = startX + (index * 4 + column) * scale;
                    int top = startY + row * scale;
                    for (int offsetY = 0; offsetY < scale; offsetY++)
                    {
                        for (int offsetX = 0; offsetX < scale; offsetX++)
                        {
                            pixels[(top + offsetY) * canvasSize +
                                left + offsetX] = -1;
                        }
                    }
                }
            }
        }
    }

    private static int GetDigitPattern(char value) => value switch
    {
        '0' => 0b111_101_101_101_111,
        '1' => 0b010_110_010_010_111,
        '2' => 0b111_001_111_100_111,
        '3' => 0b111_001_111_001_111,
        '4' => 0b101_101_111_001_001,
        '5' => 0b111_100_111_001_111,
        '6' => 0b111_100_111_101_111,
        '7' => 0b111_001_001_010_010,
        '8' => 0b111_101_111_101_111,
        '9' => 0b111_101_111_001_111,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private delegate nint WindowProcedure(
        nint window,
        uint message,
        nint wParam,
        nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id;
        internal uint Flags;
        internal uint CallbackMessage;
        internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string Tip;
        internal uint State;
        internal uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string Info;
        internal uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid Item;
        internal nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal int SizeImage;
        internal int XPixelsPerMeter;
        internal int YPixelsPerMeter;
        internal uint ColorsUsed;
        internal uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        internal bool IsIcon;
        internal uint HotspotX;
        internal uint HotspotY;
        internal nint MaskBitmap;
        internal nint ColorBitmap;
    }

#pragma warning disable SYSLIB1054
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(
        uint message,
        ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(
        [MarshalAs(UnmanagedType.LPWStr)] string message);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(
        nint window,
        int index,
        nint newValue);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(
        nint previousWindowProcedure,
        nint window,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern nint LoadIcon(nint instance, nint iconName);

    [DllImport("user32.dll", EntryPoint = "CopyIcon", SetLastError = true)]
    private static extern nint CopyIcon(nint icon);

    [DllImport("user32.dll", EntryPoint = "DestroyIcon", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", EntryPoint = "GetDC")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection")]
    private static extern nint CreateDIBSection(
        nint deviceContext,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out nint bits,
        nint section,
        uint offset);

    [DllImport("gdi32.dll", EntryPoint = "CreateBitmap")]
    private static extern nint CreateBitmap(
        int width,
        int height,
        uint planes,
        uint bitsPerPixel,
        byte[] bits);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);

    [DllImport("user32.dll", EntryPoint = "CreateIconIndirect")]
    private static extern nint CreateIconIndirect(ref IconInfo iconInfo);

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        nint window,
        int id,
        uint modifiers,
        int virtualKey);

    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
#pragma warning restore SYSLIB1054
}
