using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using GameShift.Core.Activation;

namespace GameShift.Launcher;

internal static partial class Program
{
    private const string UiExecutableName = "GameShift.UI.exe";
    private const string ElevatedSessionHostExecutableName =
        "GameShift.SessionHost.exe";
    private static readonly string[] BackgroundExecutableNames =
    [
        "GameShift.SessionHost.exe",
        "GameShift.SystemAgent.exe",
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        GameShiftLaunchOptions launchOptions;
        try
        {
            launchOptions = GameShiftLaunchOptions.Parse(args);
            if (launchOptions.GameExecutablePath is string executablePath
                && !File.Exists(executablePath))
            {
                throw new FileNotFoundException(
                    "Wybrany plik gry już nie istnieje.",
                    executablePath);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            ShowFailure(exception.Message);
            return 2;
        }

        string applicationDirectory =
            Path.GetFullPath(AppContext.BaseDirectory);
        List<string> failures = [];

        foreach (string executableName in BackgroundExecutableNames)
        {
            EnsureBackgroundProcess(
                applicationDirectory,
                executableName,
                failures);
        }

        bool uiAvailable = EnsureUserInterface(
            applicationDirectory,
            launchOptions,
            failures);
        if (failures.Count > 0)
        {
            ShowFailure(string.Join(Environment.NewLine, failures));
        }

        return uiAvailable ? 0 : 1;
    }

    private static void EnsureBackgroundProcess(
        string applicationDirectory,
        string executableName,
        List<string> failures)
    {
        string executablePath = ResolveCompanionPath(
            applicationDirectory,
            executableName);
        if (!File.Exists(executablePath))
        {
            failures.Add($"Brakuje komponentu {executableName}.");
            return;
        }

        using Process? existing =
            FindExactRunningProcess(executablePath);
        if (existing is not null)
        {
            return;
        }

        _ = TryStart(
            executablePath,
            applicationDirectory,
            createNoWindow: true,
            requireElevation: false,
            arguments: [],
            failures);
    }

    private static bool EnsureUserInterface(
        string applicationDirectory,
        GameShiftLaunchOptions launchOptions,
        List<string> failures)
    {
        string executablePath = ResolveCompanionPath(
            applicationDirectory,
            UiExecutableName);
        if (!File.Exists(executablePath))
        {
            failures.Add($"Brakuje komponentu {UiExecutableName}.");
            return false;
        }

        using Process? existing =
            FindExactRunningProcess(executablePath);
        if (existing is not null)
        {
            if (launchOptions.GameExecutablePath is string gamePath)
            {
                return TrySendActivationRequest(
                    gamePath,
                    launchOptions.StartInBackground,
                    failures);
            }

            existing.Refresh();
            nint windowHandle = existing.MainWindowHandle;
            if (windowHandle != nint.Zero)
            {
                _ = ShowWindow(windowHandle, ShowWindowMaximized);
                _ = SetForegroundWindow(windowHandle);
            }

            return true;
        }

        return TryStart(
            executablePath,
            applicationDirectory,
            createNoWindow: false,
            requireElevation: false,
            arguments: BuildUiArguments(launchOptions),
            failures);
    }

    private static List<string> BuildUiArguments(
        GameShiftLaunchOptions launchOptions)
    {
        List<string> arguments = [];
        if (launchOptions.GameExecutablePath is string executablePath)
        {
            arguments.Add("--launch-through-gameshift");
            arguments.Add(executablePath);
        }

        if (launchOptions.StartInBackground)
        {
            arguments.Add("--background");
        }

        return arguments;
    }

    private static bool TrySendActivationRequest(
        string executablePath,
        bool keepWindowHidden,
        List<string> failures)
    {
        try
        {
            string userSid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException(
                    "Windows nie zwrócił SID bieżącego użytkownika.");
            string pipeName = UiActivationProtocol.GetPipeName(userSid);
            using NamedPipeClientStream pipe = new(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.None,
                TokenImpersonationLevel.Identification);
            pipe.Connect(timeout: 5000);

            UiActivationRequest request = new(
                UiActivationProtocol.CurrentSchemaVersion,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                executablePath,
                keepWindowHidden);
            byte[] payload = UiActivationProtocol.Serialize(request);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
            pipe.Write(length);
            pipe.Write(payload);
            pipe.Flush();
            int response = pipe.ReadByte();
            if (response == 1)
            {
                return true;
            }

            failures.Add(
                "Uruchomione UI odrzuciło żądanie startu gry.");
        }
        catch (Exception exception) when (
            exception is IOException
                or TimeoutException
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            failures.Add(
                "Nie przekazano gry do uruchomionego GameShift: "
                + exception.Message);
        }

        return false;
    }

    private static bool TryStart(
        string executablePath,
        string workingDirectory,
        bool createNoWindow,
        bool requireElevation,
        IReadOnlyList<string> arguments,
        List<string> failures)
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = requireElevation,
                CreateNoWindow = createNoWindow && !requireElevation,
                WindowStyle = createNoWindow
                    ? ProcessWindowStyle.Hidden
                    : ProcessWindowStyle.Normal,
            };
            if (requireElevation)
            {
                startInfo.Verb = "runas";
            }

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process? started = Process.Start(startInfo);
            if (started is not null)
            {
                return true;
            }
        }
        catch (Win32Exception win32Exception) when (win32Exception.NativeErrorCode == 740 && !requireElevation)
        {
            return TryStart(
                executablePath,
                workingDirectory,
                createNoWindow,
                requireElevation: true,
                arguments,
                failures);
        }
        catch (Exception exception) when (
            exception is
                Win32Exception
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            failures.Add(
                $"Nie uruchomiono {Path.GetFileName(executablePath)}: "
                + exception.Message);
            return false;
        }

        failures.Add(
            $"Nie uruchomiono {Path.GetFileName(executablePath)}.");
        return false;
    }

    private static Process? FindExactRunningProcess(
        string expectedExecutablePath)
    {
        string processName =
            Path.GetFileNameWithoutExtension(expectedExecutablePath);
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            try
            {
                string? runningPath = TryGetExecutablePath(process);
                if (runningPath is not null
                    && StringComparer.OrdinalIgnoreCase.Equals(
                        Path.GetFullPath(runningPath),
                        expectedExecutablePath))
                {
                    return process;
                }
            }
            catch (Exception exception) when (
                exception is
                    Win32Exception
                    or InvalidOperationException
                    or NotSupportedException)
            {
            }

            process.Dispose();
        }

        return null;
    }

    private static unsafe string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (
            exception is
                Win32Exception
                or InvalidOperationException
                or NotSupportedException)
        {
        }

        nint handle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            process.Id);
        if (handle == nint.Zero)
        {
            return null;
        }

        try
        {
            char[] path = new char[MaximumWindowsPath];
            uint length = checked((uint)path.Length);
            fixed (char* pathPointer = path)
            {
                return QueryFullProcessImageName(
                    handle,
                    flags: 0,
                    pathPointer,
                    ref length)
                        ? new(pathPointer, 0, checked((int)length))
                        : null;
            }
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    private static string ResolveCompanionPath(
        string applicationDirectory,
        string executableName)
    {
        string path = Path.GetFullPath(
            Path.Combine(applicationDirectory, executableName));
        string? parent = Path.GetDirectoryName(path);
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                parent?.TrimEnd(Path.DirectorySeparatorChar),
                applicationDirectory.TrimEnd(
                    Path.DirectorySeparatorChar)))
        {
            throw new InvalidOperationException(
                "Companion executable escaped the application directory.");
        }

        return path;
    }

    private static void ShowFailure(string details)
    {
        _ = MessageBox(
            nint.Zero,
            "Nie udało się uruchomić wszystkich składników GameShift."
            + Environment.NewLine
            + Environment.NewLine
            + details,
            "GameShift — błąd uruchamiania",
            MessageBoxIconError);
    }

    private const int ShowWindowMaximized = 3;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaximumWindowsPath = 32_767;
    private const uint MessageBoxIconError = 0x00000010;

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(
        nint windowHandle,
        int command);

    [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(
        nint windowHandle);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "OpenProcess",
        SetLastError = true)]
    private static partial nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(
        nint processHandle,
        uint flags,
        char* executablePath,
        ref uint size);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CloseHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport(
        "user32.dll",
        EntryPoint = "MessageBoxW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(
        nint windowHandle,
        string text,
        string caption,
        uint type);
}
