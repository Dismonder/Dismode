using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GameShift.Windows.Processes;

namespace GameShift.IntegrationTests.Processes;

[TestClass]
public sealed class DesktopUserProcessStarterTests
{
    private const int ErrorFileNotFound = 2;
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void ChildOfARequestedParentInheritsThatParent()
    {
        using StartedProcess anchor =
            DesktopUserProcessStarter.StartAsChildOf(
                Sleeper(),
                Environment.ProcessId);
        try
        {
            // Powinowactwo dziedziczy sie ta sama droga co token; jego
            // zmiane widac bez uprawnien, wiec dowodzi, ze atrybut rodzica
            // naprawde zadzialal, a nie tylko przepisal numer PID.
            nint anchorAffinity = SetSingleCoreAffinity(anchor.Id);

            using StartedProcess child =
                DesktopUserProcessStarter.StartAsChildOf(Sleeper(), anchor.Id);
            try
            {
                Assert.IsTrue(child.InheritsParentToken);
                Assert.IsNull(child.FallbackReason);
                Assert.IsFalse(child.HasExited);
                Assert.AreEqual(anchor.Id, ReadParentProcessId(child.Id));
                Assert.AreEqual(
                    Environment.ProcessId,
                    ReadParentProcessId(anchor.Id));
                using Process childProcess = Process.GetProcessById(child.Id);
                Assert.AreEqual(anchorAffinity, childProcess.ProcessorAffinity);
            }
            finally
            {
                Terminate(child.Id);
            }

            Assert.IsTrue(
                SpinWait.SpinUntil(() => child.HasExited, ExitWait),
                "Zakonczone dziecko ma zglaszac HasExited.");
        }
        finally
        {
            Terminate(anchor.Id);
        }
    }

    [TestMethod]
    public void StartHandsTheTokenToTheShellOnlyWhenElevated()
    {
        using StartedProcess process = DesktopUserProcessStarter.Start(Sleeper());
        try
        {
            int parent = ReadParentProcessId(process.Id);
            if (DesktopUserProcessStarter.IsCurrentProcessElevated())
            {
                int? shell = DesktopUserProcessStarter.TryFindDesktopShellProcessId();
                Assert.IsNotNull(shell);
                Assert.IsTrue(process.InheritsParentToken, process.FallbackReason);
                Assert.AreEqual(shell.Value, parent);
            }
            else
            {
                Assert.IsFalse(process.InheritsParentToken);
                Assert.IsNull(process.FallbackReason);
                Assert.AreEqual(Environment.ProcessId, parent);
            }
        }
        finally
        {
            Terminate(process.Id);
        }
    }

    [TestMethod]
    public void WorkingDirectoryAndArgumentsReachTheChild()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "GameShift.Starter." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("cd>katalog.txt");

            using StartedProcess process =
                DesktopUserProcessStarter.StartAsChildOf(
                    startInfo,
                    Environment.ProcessId);
            Assert.IsTrue(
                SpinWait.SpinUntil(() => process.HasExited, ExitWait),
                "cmd ma sie zakonczyc.");
            string reported = File.ReadAllText(
                Path.Combine(directory, "katalog.txt")).Trim();
            Assert.AreEqual(
                Path.TrimEndingDirectorySeparator(directory),
                Path.TrimEndingDirectorySeparator(reported),
                ignoreCase: true);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void MissingProgramFailsWithTheWindowsError()
    {
        ProcessStartInfo missing = new()
        {
            FileName = Path.Combine(
                Environment.SystemDirectory,
                "gameshift-nie-ma-takiego-programu.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Win32Exception exception = Assert.ThrowsExactly<Win32Exception>(() =>
            DesktopUserProcessStarter.StartAsChildOf(
                missing,
                Environment.ProcessId));

        Assert.AreEqual(ErrorFileNotFound, exception.NativeErrorCode);
        Assert.Contains("CreateProcess", exception.Message);
    }

    [TestMethod]
    public void RelativePathsAndShellExecuteAreRefused()
    {
        ProcessStartInfo relative = new()
        {
            FileName = "ping.exe",
            UseShellExecute = false,
        };
        ProcessStartInfo shell = new()
        {
            FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"),
            UseShellExecute = true,
        };

        Assert.ThrowsExactly<ArgumentException>(() =>
            DesktopUserProcessStarter.StartAsChildOf(
                relative,
                Environment.ProcessId));
        Assert.ThrowsExactly<ArgumentException>(() =>
            DesktopUserProcessStarter.StartAsChildOf(
                shell,
                Environment.ProcessId));
    }

    private static ProcessStartInfo Sleeper()
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"),
            WorkingDirectory = Environment.SystemDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("60");
        startInfo.ArgumentList.Add("127.0.0.1");
        return startInfo;
    }

    private static nint SetSingleCoreAffinity(int processId)
    {
        using Process process = Process.GetProcessById(processId);
        nint mask = process.ProcessorAffinity;
        nint singleCore = mask & (nint)(-mask);
        process.ProcessorAffinity = singleCore;
        return process.ProcessorAffinity;
    }

    private static void Terminate(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill();
            process.WaitForExit((int)ExitWait.TotalMilliseconds);
        }
        catch (Exception exception) when (
            exception is
                ArgumentException
                or InvalidOperationException
                or Win32Exception)
        {
        }
    }

    private static int ReadParentProcessId(int processId)
    {
        using Process process = Process.GetProcessById(processId);
        int status = NtQueryInformationProcess(
            process.Handle,
            0,
            out ProcessBasicInformation information,
            (uint)Marshal.SizeOf<ProcessBasicInformation>(),
            out _);
        Assert.AreEqual(0, status, "NtQueryInformationProcess");
        return checked((int)information.InheritedFromUniqueProcessId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    // DllImport, nie LibraryImport: projekt testow nie wlacza kodu
    // niebezpiecznego, ktorego wymaga generator tego drugiego.
#pragma warning disable SYSLIB1054
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        nint processHandle,
        int informationClass,
        out ProcessBasicInformation information,
        uint informationLength,
        out uint returnLength);
#pragma warning restore SYSLIB1054
}
