using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace GameShift.SystemOptimizer;

[SuppressMessage(
    "Design",
    "CA1001",
    Justification = "The WinUI application lifecycle releases the single-instance mutex when its window closes.")]
public partial class App : Application
{
    private const string ActivationSignalName =
        "Local\\GameShift.SystemOptimizer.Activate";

    private Window? _window;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationSignal;
    private RegisteredWaitHandle? _activationRegistration;

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

        _activationSignal = new(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationSignalName);
        _instanceMutex = new(
            initiallyOwned: true,
            "Local\\GameShift.SystemOptimizer",
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

        _window = new MainWindow();
        _window.Closed += Window_Closed;
        _window.Activate();
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
        Window? window = _window;
        if (window is null)
        {
            return;
        }

        _ = window.DispatcherQueue.TryEnqueue(() =>
        {
            if (_window is null)
            {
                return;
            }

            nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(
                _window);
            _ = ShowWindow(windowHandle, 9);
            _ = SetForegroundWindow(windowHandle);
        });
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        if (_window is not null)
        {
            _window.Closed -= Window_Closed;
            _window = null;
        }

        _activationRegistration?.Unregister(null);
        _activationRegistration = null;
        _activationSignal?.Dispose();
        _activationSignal = null;
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _instanceMutex = null;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);
}
