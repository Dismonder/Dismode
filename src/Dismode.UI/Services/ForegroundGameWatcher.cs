using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dismode.Windows.Processes;

namespace Dismode.UI.Services;

public sealed record ForegroundGameSample(
    int? ProcessId,
    string ProcessName,
    double? FramesPerSecond,
    double? FrameTimeMilliseconds,
    bool IsPresenting);

/// <summary>
/// Watches whatever the user is actually looking at and reports its frame rate,
/// with or without a Dismode session.
/// <para>
/// The overlay used to draw only from session telemetry, so a game started
/// outside Dismode left it showing dashes — which reads as broken rather than
/// as "not measuring", and is the wrong behaviour for something meant to sit on
/// top of a game the way RivaTuner's does.
/// </para>
/// <para>
/// Deciding what counts as a game is done by asking rather than guessing: the
/// foreground window's process is handed to PresentMon, and if frames come back
/// it was a game. A list of titles would be wrong the day after it was written.
/// Shell and desktop processes are skipped outright, because they never present
/// through a swap chain we can see and asking about them only costs a capture
/// session.
/// </para>
/// </summary>
public sealed class ForegroundGameWatcher : IAsyncDisposable
{
    private static readonly HashSet<string> NeverGames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer",
            "ApplicationFrameHost",
            "ShellExperienceHost",
            "StartMenuExperienceHost",
            "SearchHost",
            "TextInputHost",
            "SystemSettings",
            "Taskmgr",
            "Dismode.UI",
            "Dismode",
        };

    private readonly IFrameRateProvider _frameRates;
    private int? _attachedProcessId;
    private bool _disposed;

    public ForegroundGameWatcher(IFrameRateProvider? frameRates = null)
    {
        _frameRates = frameRates ?? new PresentMonFrameRateProvider();
    }

    public async ValueTask<ForegroundGameSample> SampleAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        (int? processId, string processName) = ReadForegroundProcess();
        if (processId is not int candidate)
        {
            await DetachAsync(cancellationToken).ConfigureAwait(false);
            return new(null, processName, null, null, false);
        }

        if (_attachedProcessId != candidate)
        {
            await DetachAsync(cancellationToken).ConfigureAwait(false);
            _attachedProcessId = candidate;
        }

        try
        {
            FrameRateSample sample = await _frameRates
                .SampleAsync([candidate], cancellationToken)
                .ConfigureAwait(false);
            return new(
                candidate,
                processName,
                sample.FramesPerSecond,
                sample.FrameTimeMilliseconds,
                sample.FramesPerSecond is > 0);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or Win32Exception
                or IOException
                or TimeoutException)
        {
            return new(candidate, processName, null, null, false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _frameRates.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask DetachAsync(CancellationToken cancellationToken)
    {
        if (_attachedProcessId is null)
        {
            return;
        }

        _attachedProcessId = null;
        try
        {
            await _frameRates.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or Win32Exception
                or IOException
                or TimeoutException)
        {
        }
    }

    private static (int? ProcessId, string ProcessName) ReadForegroundProcess()
    {
        nint window = GetForegroundWindow();
        if (window == nint.Zero)
        {
            return (null, string.Empty);
        }

        _ = GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0)
        {
            return (null, string.Empty);
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string name = process.ProcessName;
            return NeverGames.Contains(name)
                ? (null, name)
                : ((int?)process.Id, name);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or Win32Exception)
        {
            return (null, string.Empty);
        }
    }

    // DllImport, nie LibraryImport: generator tego drugiego wymaga
    // niebezpiecznego kodu, ktorego projekt interfejsu nie wlacza.
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out uint processId);
}
