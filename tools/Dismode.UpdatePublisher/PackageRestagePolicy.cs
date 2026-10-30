namespace Dismode.UpdatePublisher;

internal static class PackageRestagePolicy
{
    /// <summary>
    /// Package chunks are served as immutable, so an already published
    /// version may only be restaged with byte-identical chunks.
    /// </summary>
    internal static void EnsureIdenticalPackage(
        string existingDirectory,
        string stagedDirectory)
    {
        string[] existing = ListFiles(existingDirectory);
        string[] staged = ListFiles(stagedDirectory);
        if (!existing.SequenceEqual(staged, StringComparer.Ordinal))
        {
            throw Refusal();
        }

        foreach (string name in existing)
        {
            if (!FilesEqual(
                    Path.Combine(existingDirectory, name),
                    Path.Combine(stagedDirectory, name)))
            {
                throw Refusal();
            }
        }
    }

    private static InvalidDataException Refusal() => new(
        "This version is already published with different package chunks. "
        + "Chunks are served as immutable, so they cannot be replaced; "
        + "publish a new version instead.");

    private static string[] ListFiles(string directory) =>
        [.. Directory.EnumerateFileSystemEntries(directory)
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)];

    private static bool FilesEqual(string left, string right)
    {
        FileInfo leftInfo = new(left);
        FileInfo rightInfo = new(right);
        if (!leftInfo.Exists
            || !rightInfo.Exists
            || leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        using FileStream leftStream = leftInfo.OpenRead();
        using FileStream rightStream = rightInfo.OpenRead();
        byte[] leftBuffer = new byte[81920];
        byte[] rightBuffer = new byte[81920];
        while (true)
        {
            int read = leftStream.ReadAtLeast(
                leftBuffer,
                leftBuffer.Length,
                throwOnEndOfStream: false);
            int rightRead = rightStream.ReadAtLeast(
                rightBuffer,
                rightBuffer.Length,
                throwOnEndOfStream: false);
            if (read != rightRead
                || !leftBuffer.AsSpan(0, read).SequenceEqual(
                    rightBuffer.AsSpan(0, rightRead)))
            {
                return false;
            }

            if (read == 0)
            {
                return true;
            }
        }
    }
}
