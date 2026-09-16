using System.Runtime.InteropServices;
using GameShift.Windows.Processes;

namespace GameShift.IntegrationTests.Processes;

[TestClass]
public sealed class WindowsCommandLineTests
{
    [TestMethod]
    public void PlainArgumentsStayBareAndTheRestGetQuoted()
    {
        Assert.AreEqual("a.exe b", WindowsCommandLine.Build("a.exe", ["b"]));
        Assert.AreEqual(
            "a.exe \"b c\"",
            WindowsCommandLine.Build("a.exe", ["b c"]));
        Assert.AreEqual("a.exe \"\"", WindowsCommandLine.Build("a.exe", [""]));
        Assert.AreEqual(
            "a.exe \"b\\\"c\"",
            WindowsCommandLine.Build("a.exe", ["b\"c"]));
        Assert.AreEqual("a.exe b\\", WindowsCommandLine.Build("a.exe", ["b\\"]));
        Assert.AreEqual(
            "a.exe \"b c\\\\\"",
            WindowsCommandLine.Build("a.exe", ["b c\\"]));
        Assert.AreEqual(
            "\"C:\\Program Files\\a.exe\" -x",
            WindowsCommandLine.Build("C:\\Program Files\\a.exe", ["-x"]));
    }

    [TestMethod]
    public void RoundTripsThroughCommandLineToArgv()
    {
        string program = @"C:\Games\Some Game\game.exe";
        string[] arguments =
        [
            "plain",
            "with space",
            "",
            "quote\"inside",
            "back\\slash",
            "trailing\\",
            "both \\\" mixed\\\\",
            "tab\tchar",
            @"C:\Program Files\Game\game.exe",
            "--name=\"x y\"",
            "\"",
            "\\\\",
            " leading and trailing ",
        ];

        string commandLine = WindowsCommandLine.Build(program, arguments);
        string[] parsed = Parse(commandLine);

        string[] expected = [program, .. arguments];
        CollectionAssert.AreEqual(expected, parsed);
    }

    private static string[] Parse(string commandLine)
    {
        nint argv = CommandLineToArgvW(commandLine, out int count);
        Assert.AreNotEqual(nint.Zero, argv, "CommandLineToArgvW");
        try
        {
            string[] result = new string[count];
            for (int index = 0; index < count; index++)
            {
                result[index] = Marshal.PtrToStringUni(
                    Marshal.ReadIntPtr(argv, index * nint.Size))
                    ?? string.Empty;
            }

            return result;
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    // DllImport, nie LibraryImport: projekt testow nie wlacza kodu
    // niebezpiecznego, ktorego wymaga generator tego drugiego.
#pragma warning disable SYSLIB1054
    [DllImport(
        "shell32.dll",
        EntryPoint = "CommandLineToArgvW",
        CharSet = CharSet.Unicode)]
    private static extern nint CommandLineToArgvW(
        string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
#pragma warning restore SYSLIB1054
}
