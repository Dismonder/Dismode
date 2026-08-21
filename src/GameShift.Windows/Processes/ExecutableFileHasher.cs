using System.Security.Cryptography;

namespace GameShift.Windows.Processes;

public static class ExecutableFileHasher
{
    public static async ValueTask<string> ComputeSha256Async(
        string executablePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException(
                "The executable path must be fully qualified.",
                nameof(executablePath));
        }

        FileStreamOptions options = new()
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        await using FileStream stream = new(executablePath, options);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
