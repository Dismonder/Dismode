using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using GameShift.MemoryOptimizer.ComponentControl;
using GameShift.MemoryOptimizer.Core;
using GameShift.MemoryOptimizer.Core.Models;
using Microsoft.UI.Xaml;

namespace GameShift.MemoryOptimizer;

[SuppressMessage(
    "Design",
    "CA1001",
    Justification = "The WinUI application lifecycle explicitly disposes the tray icon before Exit().")]
public partial class App : Application
{
    private const string ActivationSignalName =
        "Local\\GameShift.MemoryOptimizer.Activate";

    private MainWindow? _window;
    private TrayIconService? _tray;
    private TrayMenuWindow? _trayMenu;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationSignal;
    private RegisteredWaitHandle? _activationRegistration;
    private bool _exitInProgress;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22631))
        {
            Exit();
            return;
        }

        string[] arguments = Environment.GetCommandLineArgs()[1..];
        if (arguments.Contains(
                "--disable-component",
                StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode =
                MemoryOptimizerComponentShutdown.RunElevatedDisable();
            Exit();
            return;
        }

        if (arguments.Contains(
                "--startup-probe",
                StringComparer.OrdinalIgnoreCase))
        {
            TrayMenuWindow? probeMenu = null;
            try
            {
                _window = new();
                MemoryOptimizerStatus probeStatus = new(false, false,
                    new(DateTimeOffset.UtcNow, 16UL << 30, 4UL << 30, 128UL << 40, 127UL << 40, 75),
                    new(), null, "0.4.0", "");
                probeMenu = new(probeStatus, true, true,
                    static () => { }, static () => Task.CompletedTask, static () => Task.CompletedTask,
                    static () => Task.FromResult(false), static () => { });
                probeMenu.VerifyLayoutForStartupProbe();
                probeMenu.UpdateState(null, false, false);
                probeMenu.VerifyLayoutForStartupProbe();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                Environment.ExitCode = 1;
            }
            finally
            {
                probeMenu?.Close();
                _window?.AllowCloseAndClose();
                _window = null;
            }
            Exit();
            return;
        }

        _activationSignal = new(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationSignalName);
        _instanceMutex = new(
            initiallyOwned: true,
            "Local\\GameShift.MemoryOptimizer",
            out bool createdNew);
        if (!createdNew)
        {
            _ = _activationSignal.Set();
            _activationSignal.Dispose();
            _activationSignal = null;
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Exit();
            return;
        }

        bool startInBackground = arguments.Contains(
            "--background",
            StringComparer.OrdinalIgnoreCase);
        bool startedAtLogon = arguments.Contains(
            "--startup",
            StringComparer.OrdinalIgnoreCase);
        _window = new();
        _window.Activate();
        RegisterActivationSignal();
        _tray = TryCreateTray(_window);
        if (_tray is { } tray)
        {
            _window.TrayStatusChanged += (_, status) =>
            {
                tray.UpdateMemory(
                    status.MemoryLoadPercent,
                    status.IsPaused);
                _trayMenu?.UpdateState(
                    _window.CurrentTrayStatus,
                    _window.CanOptimizeFromTray,
                    _window.CanPauseFromTray);
            };
            _window.SettingsApplied += (_, settings) =>
            {
                if (!tray.UpdateHotkey(settings.Settings.Hotkey))
                {
                    tray.ShowNotification(
                        "GameShift Memory Optimizer",
                        "Wybrany skrót jest zajęty. Pozostawiono poprzedni skrót.");
                }
            };
            _window.TrayNotificationRequested += (_, notification) =>
                tray.ShowNotification(notification.Title, notification.Message);
        }

        if ((startInBackground || startedAtLogon) && _tray is not null)
        {
            _window.HideToTray();
        }

        _ = InitializeWindowAsync(startedAtLogon);
    }

    private TrayIconService? TryCreateTray(MainWindow window)
    {
        try
        {
            return new(
                window.WindowHandle,
                window.DispatcherQueue,
                window.ShowFromTray,
                () => ShowTrayMenu(window),
                () => _ = window.OptimizeNowAsync());
        }
        catch (InvalidOperationException exception)
        {
            Debug.WriteLine(
                "Memory Optimizer tray initialization failed: " +
                exception.Message);
            return null;
        }
    }

    private void ShowTrayMenu(MainWindow window)
    {
        if (_exitInProgress)
        {
            return;
        }

        if (_trayMenu is { } existing)
        {
            existing.UpdateState(window.CurrentTrayStatus, window.CanOptimizeFromTray, window.CanPauseFromTray);
            existing.ShowAtCursor();
            return;
        }

        TrayMenuWindow menu = new(
            window.CurrentTrayStatus,
            window.CanOptimizeFromTray,
            window.CanPauseFromTray,
            () =>
            {
                CloseTrayMenu();
                window.ShowFromTray();
            },
            async () =>
            {
                await window.OptimizeNowAsync().ConfigureAwait(true);
            },
            async () =>
            {
                await window.TogglePauseAsync().ConfigureAwait(true);
            },
            DisableComponentAsync,
            () =>
            {
                CloseTrayMenu();
                ExitInterface();
            });
        _trayMenu = menu;
        menu.Dismissed += TrayMenu_Dismissed;
        menu.ShowAtCursor();
    }

    private void TrayMenu_Dismissed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _trayMenu))
        {
            _trayMenu = null;
        }
    }

    private void CloseTrayMenu()
    {
        TrayMenuWindow? menu = _trayMenu;
        if (menu is null)
        {
            return;
        }

        _trayMenu = null;
        menu.Dismissed -= TrayMenu_Dismissed;
        menu.CloseMenu();
    }

    private async Task<bool> DisableComponentAsync()
    {
        if (_window is not null &&
            !await _window.ConfirmExitAsync().ConfigureAwait(true))
        {
            return false;
        }

        MemoryOptimizerComponentDisableResult result =
            await MemoryOptimizerComponentShutdown.DisableAsync(
                CancellationToken.None).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.Message);
        }

        CloseTrayMenu();
        await ExitInterfaceAsync().ConfigureAwait(true);
        return true;
    }

    private void RegisterActivationSignal()
    {
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationSignal!,
            static (state, timedOut) =>
            {
                if (!timedOut && state is App application)
                {
                    application.QueueActivation();
                }
            },
            this,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);
    }

    private void QueueActivation()
    {
        MainWindow? window = _window;
        if (window is not null)
        {
            _ = window.DispatcherQueue.TryEnqueue(window.ShowFromTray);
        }
    }

    private async Task InitializeWindowAsync(bool startedAtLogon)
    {
        if (_window is null)
        {
            return;
        }

        await _window.InitializeAsync().ConfigureAwait(true);
        if (startedAtLogon && !_window.StartWithWindowsSetting)
        {
            await ExitInterfaceAsync().ConfigureAwait(true);
            return;
        }

        if (startedAtLogon && !_window.StartMinimizedSetting)
        {
            _window.ShowFromTray();
        }
    }

    private void ExitInterface() => _ = ExitInterfaceAsync();

    private async Task ExitInterfaceAsync()
    {
        if (_exitInProgress)
        {
            return;
        }

        _exitInProgress = true;
        try
        {
            CloseTrayMenu();
            if (_window is not null &&
                !await _window.ConfirmExitAsync().ConfigureAwait(true))
            {
                return;
            }

            if (_window is not null)
            {
                try
                {
                    await _window.AuthorizeTrayShutdownAsync().ConfigureAwait(true);
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidOperationException or
                        TimeoutException or OperationCanceledException)
                {
                    Debug.WriteLine(
                        "Memory Optimizer tray shutdown acknowledgement failed: " +
                        exception.Message);
                }
            }

            _tray?.Dispose();
            _tray = null;
            _window?.AllowCloseAndClose();
            _window = null;
            _activationRegistration?.Unregister(null);
            _activationRegistration = null;
            _activationSignal?.Dispose();
            _activationSignal = null;
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
            _instanceMutex = null;
            Exit();
        }
        finally
        {
            if (_window is not null)
            {
                _exitInProgress = false;
            }
        }
    }

}
