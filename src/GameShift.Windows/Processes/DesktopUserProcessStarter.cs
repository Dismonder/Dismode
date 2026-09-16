using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace GameShift.Windows.Processes;

/// <summary>
/// Uruchamia program z tokenem zwyklego uzytkownika pulpitu, nawet gdy
/// wywolujacy dziala podniesiony. Process.Start dziedziczy token
/// wywolujacego, wiec gra albo Discord odpalone z uprzywilejowanego
/// GameShift.SessionHost dzialalyby jako administrator: zapisy gry z
/// cudzymi uprawnieniami, przeciaganie plikow z Eksploratora odrzucane
/// przez UIPI, nakladki i aktualizatory sklepow odmawiajace pracy w
/// podniesionym procesie, i pelne prawa tam, gdzie nikt o nie nie prosil.
/// Dlatego nowy proces dostaje za rodzica powloke (Eksploratora): atrybut
/// PROC_THREAD_ATTRIBUTE_PARENT_PROCESS sprawia, ze dziedziczy jej token,
/// czyli dokladnie ten, z ktorym uzytkownik klika dwa razy w ikone. Gdy nie
/// ma czego oddawac (proces niepodniesiony) albo powloka jest niedostepna,
/// program startuje zwyczajnie, a powod trafia do
/// <see cref="StartedProcess.FallbackReason"/>.
/// </summary>
public static unsafe partial class DesktopUserProcessStarter
{
    private const uint ProcessCreateProcess = 0x0080;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindowFlag = 0x08000000;
    private const uint StartupFlagUseShowWindow = 0x00000001;
    private const nuint ProcThreadAttributeParentProcess = 0x00020000;
    private const ushort ShowWindowHide = 0;
    private const ushort ShowWindowNormal = 1;
    private const ushort ShowWindowMinimized = 2;
    private const ushort ShowWindowMaximized = 3;

    public static StartedProcess Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!IsCurrentProcessElevated())
        {
            return StartDirectly(startInfo, fallbackReason: null);
        }

        int? shellProcessId = TryFindDesktopShellProcessId();
        if (shellProcessId is null)
        {
            return StartDirectly(
                startInfo,
                "Brak procesu powłoki pulpitu, od którego można odziedziczyć "
                + "token użytkownika.");
        }

        try
        {
            return StartAsChildOf(startInfo, shellProcessId.Value);
        }
        catch (Win32Exception exception)
        {
            return StartDirectly(startInfo, exception.Message);
        }
    }

    /// <summary>
    /// Tworzy proces jako dziecko wskazanego rodzica; dziecko dziedziczy
    /// jego token, priorytet i powinowactwo. Bez awaryjnego zwyklego startu:
    /// niepowodzenie wychodzi jako <see cref="Win32Exception"/>.
    /// </summary>
    public static StartedProcess StartAsChildOf(
        ProcessStartInfo startInfo,
        int parentProcessId)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentProcessId);
        if (startInfo.UseShellExecute)
        {
            throw new ArgumentException(
                "UseShellExecute nie łączy się z wyborem rodzica procesu.",
                nameof(startInfo));
        }

        if (!Path.IsPathRooted(startInfo.FileName))
        {
            throw new ArgumentException(
                "Program musi być podany pełną ścieżką.",
                nameof(startInfo));
        }

        if (startInfo.ArgumentList.Count > 0
            && !string.IsNullOrEmpty(startInfo.Arguments))
        {
            throw new ArgumentException(
                "ArgumentList i Arguments wykluczają się.",
                nameof(startInfo));
        }

        nint parent = OpenProcess(
            ProcessCreateProcess,
            inheritHandle: false,
            parentProcessId);
        if (parent == nint.Zero)
        {
            throw Failure($"OpenProcess({parentProcessId})");
        }

        try
        {
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref size);
            if (size == 0)
            {
                throw Failure("InitializeProcThreadAttributeList");
            }

            void* attributeList = NativeMemory.AllocZeroed(size);
            try
            {
                if (!InitializeProcThreadAttributeList(
                        (nint)attributeList,
                        1,
                        0,
                        ref size))
                {
                    throw Failure("InitializeProcThreadAttributeList");
                }

                try
                {
                    nint parentHandle = parent;
                    if (!UpdateProcThreadAttribute(
                            (nint)attributeList,
                            0,
                            ProcThreadAttributeParentProcess,
                            &parentHandle,
                            (nuint)sizeof(nint),
                            null,
                            null))
                    {
                        throw Failure("UpdateProcThreadAttribute");
                    }

                    return Create(startInfo, (nint)attributeList);
                }
                finally
                {
                    DeleteProcThreadAttributeList((nint)attributeList);
                }
            }
            finally
            {
                NativeMemory.Free(attributeList);
            }
        }
        finally
        {
            _ = CloseHandle(parent);
        }
    }

    public static bool IsCurrentProcessElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Proces powloki pulpitu: wlasciciel okna pulpitu, a gdy powloka nie ma
    /// jeszcze okna (tuz po restarcie Eksploratora), najstarszy explorer.exe
    /// w tej sesji. Null, gdy pulpit obsluguje inna powloka.
    /// </summary>
    public static int? TryFindDesktopShellProcessId()
    {
        nint shellWindow = GetShellWindow();
        if (shellWindow != nint.Zero)
        {
            _ = GetWindowThreadProcessId(shellWindow, out uint windowProcessId);
            if (windowProcessId != 0)
            {
                return checked((int)windowProcessId);
            }
        }

        int sessionId;
        using (Process current = Process.GetCurrentProcess())
        {
            sessionId = current.SessionId;
        }

        int? oldestProcessId = null;
        DateTime oldestStart = DateTime.MaxValue;
        foreach (Process explorer in Process.GetProcessesByName("explorer"))
        {
            using (explorer)
            {
                try
                {
                    if (explorer.SessionId != sessionId)
                    {
                        continue;
                    }

                    DateTime started = explorer.StartTime;
                    if (started < oldestStart)
                    {
                        oldestStart = started;
                        oldestProcessId = explorer.Id;
                    }
                }
                catch (Exception exception) when (
                    exception is
                        InvalidOperationException
                        or Win32Exception
                        or NotSupportedException)
                {
                }
            }
        }

        return oldestProcessId;
    }

    private static StartedProcess StartDirectly(
        ProcessStartInfo startInfo,
        string? fallbackReason)
    {
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Windows nie uruchomił programu {startInfo.FileName}.");
        return new StartedProcess(process, fallbackReason);
    }

    private static StartedProcess Create(
        ProcessStartInfo startInfo,
        nint attributeList)
    {
        string commandLine = WindowsCommandLine.Build(
            startInfo.FileName,
            startInfo.ArgumentList);
        if (startInfo.ArgumentList.Count == 0
            && !string.IsNullOrWhiteSpace(startInfo.Arguments))
        {
            commandLine = commandLine + " " + startInfo.Arguments;
        }

        // CreateProcessW moze modyfikowac wiersz polecen, wiec dostaje
        // wlasny, zakonczony zerem bufor, a nie wnetrze lancucha .NET.
        char[] commandLineBuffer = new char[commandLine.Length + 1];
        commandLine.CopyTo(0, commandLineBuffer, 0, commandLine.Length);
        string? workingDirectory =
            string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
                ? null
                : startInfo.WorkingDirectory;

        uint creationFlags = ExtendedStartupInfoPresent;
        if (startInfo.CreateNoWindow)
        {
            creationFlags |= CreateNoWindowFlag;
        }

        StartupInfoEx startup = default;
        startup.StartupInfo.Size = (uint)sizeof(StartupInfoEx);
        startup.AttributeList = attributeList;
        if (startInfo.WindowStyle != ProcessWindowStyle.Normal)
        {
            startup.StartupInfo.Flags = StartupFlagUseShowWindow;
            startup.StartupInfo.ShowWindow = startInfo.WindowStyle switch
            {
                ProcessWindowStyle.Hidden => ShowWindowHide,
                ProcessWindowStyle.Minimized => ShowWindowMinimized,
                ProcessWindowStyle.Maximized => ShowWindowMaximized,
                _ => ShowWindowNormal,
            };
        }

        ProcessInformation information = default;
        bool created;
        fixed (char* applicationName = startInfo.FileName)
        fixed (char* commandLinePointer = commandLineBuffer)
        fixed (char* directory = workingDirectory)
        {
            created = CreateProcessW(
                applicationName,
                commandLinePointer,
                null,
                null,
                inheritHandles: false,
                creationFlags,
                null,
                directory,
                &startup,
                &information);
        }

        if (!created)
        {
            throw Failure($"CreateProcess {startInfo.FileName}");
        }

        _ = CloseHandle(information.Thread);
        return new StartedProcess(
            checked((int)information.ProcessId),
            new SafeProcessHandle(information.Process, ownsHandle: true));
    }

    private static Win32Exception Failure(string operation)
    {
        int error = Marshal.GetLastPInvokeError();
        return new Win32Exception(
            error,
            $"{operation}: {new Win32Exception(error).Message}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public char* Reserved;
        public char* Desktop;
        public char* Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public byte* Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        nint attributeList,
        uint attributeCount,
        uint flags,
        ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nuint attribute,
        void* value,
        nuint size,
        void* previousValue,
        nuint* returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(
        nint attributeList);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        char* applicationName,
        char* commandLine,
        void* processAttributes,
        void* threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        void* environment,
        char* currentDirectory,
        StartupInfoEx* startupInfo,
        ProcessInformation* processInformation);
}
