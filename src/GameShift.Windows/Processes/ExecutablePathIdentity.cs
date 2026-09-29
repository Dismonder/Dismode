using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameShift.Windows.Processes;

/// <summary>
/// Decides whether a path written down by a game library and a path Windows
/// reports for a running process name the same file.
/// <para>
/// The two sides are not produced the same way. A library path is whatever
/// Steam, Epic, GOG or the user's file picker wrote down; the path behind a
/// running process comes from the kernel, which resolved every junction and
/// symbolic link while opening the image. A moved Steam library — the usual
/// way a second drive gets used, and the one Playnite hit in issue #913 —
/// leaves the first form pointing through a junction and the second form
/// pointing past it. Compared as text they differ, and a plain comparison
/// then says "not the same game" about the same file on disk.
/// </para>
/// <para>
/// Comparison starts with the text, so the ordinary installation costs
/// nothing. Only when the text differs is the file opened, and only to ask
/// Windows for its final name. A path that cannot be opened — gone, on a
/// disconnected share, refused — resolves to nothing and the comparison
/// stays negative: the same answer the plain comparison gave before.
/// </para>
/// </summary>
public static partial class ExecutablePathIdentity
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;

    /// <summary>
    /// Lets the handle name a directory as well as a file; without it
    /// CreateFile refuses directories outright.
    /// </summary>
    private const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>The final name, with a drive letter rather than a volume GUID.</summary>
    private const uint FileNameNormalized = 0x00000000;
    private const uint VolumeNameDos = 0x00000000;

    private const int InitialPathLength = 260;
    private const int MaximumWindowsPath = 32767;
    private const string ExtendedLengthPrefix = @"\\?\";
    private const string ExtendedLengthUncPrefix = @"\\?\UNC\";

    /// <summary>
    /// True when both paths name the same executable. Null on either side is
    /// never a match: a path GameShift could not read is not evidence.
    /// </summary>
    public static bool AreSameExecutable(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        if (StringComparer.OrdinalIgnoreCase.Equals(left, right))
        {
            return true;
        }

        string? leftFinal = TryResolveFinalPath(left);
        if (leftFinal is null)
        {
            return false;
        }

        string? rightFinal = TryResolveFinalPath(right);
        return rightFinal is not null
            && StringComparer.OrdinalIgnoreCase.Equals(leftFinal, rightFinal);
    }

    /// <summary>
    /// The path with every junction and symbolic link resolved, or the path
    /// itself when Windows cannot answer. For callers that compare one
    /// library path against many kernel-supplied ones and would otherwise pay
    /// for the same resolution over and over.
    /// </summary>
    public static string ResolveFinalPathOrSelf(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return TryResolveFinalPath(path) ?? path;
    }

    /// <summary>
    /// The path Windows reports for the file behind <paramref name="path"/>,
    /// or null when it cannot be opened. Works for a running executable: the
    /// handle asks for no access at all, only for the name.
    /// </summary>
    public static unsafe string? TryResolveFinalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using SafeFileHandle handle = CreateFile(
            path,
            desiredAccess: 0,
            FileShareRead | FileShareWrite | FileShareDelete,
            securityAttributes: nint.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            templateFile: nint.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        char[] buffer = new char[InitialPathLength];
        uint length = ReadFinalPath(handle, buffer);
        if (length == 0)
        {
            return null;
        }

        // Za krotki bufor: funkcja zwraca wtedy dlugosc POTRZEBNA razem
        // ze znakiem konczacym, wiec wynik rowny dlugosci bufora tez znaczy
        // „za malo miejsca", nie „dokladnie sie zmiescilo".
        if (length >= (uint)buffer.Length)
        {
            if (length > MaximumWindowsPath)
            {
                return null;
            }

            buffer = new char[length];
            length = ReadFinalPath(handle, buffer);
            if (length == 0 || length >= (uint)buffer.Length)
            {
                return null;
            }
        }

        return StripExtendedLengthPrefix(
            new string(buffer, 0, checked((int)length)));
    }

    private static unsafe uint ReadFinalPath(
        SafeFileHandle handle,
        char[] buffer)
    {
        fixed (char* bufferPointer = buffer)
        {
            return GetFinalPathNameByHandle(
                handle,
                bufferPointer,
                checked((uint)buffer.Length),
                FileNameNormalized | VolumeNameDos);
        }
    }

    /// <summary>
    /// Drops the extended-length prefix Windows puts in front of a final
    /// path, so the result compares against paths written down by a launcher.
    /// </summary>
    private static string StripExtendedLengthPrefix(string path)
    {
        if (path.StartsWith(ExtendedLengthUncPrefix, StringComparison.Ordinal))
        {
            return string.Concat(
                @"\\",
                path.AsSpan(ExtendedLengthUncPrefix.Length));
        }

        return path.StartsWith(ExtendedLengthPrefix, StringComparison.Ordinal)
            ? path[ExtendedLengthPrefix.Length..]
            : path;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(
        SafeFileHandle handle,
        char* filePath,
        uint filePathLength,
        uint flags);
}
