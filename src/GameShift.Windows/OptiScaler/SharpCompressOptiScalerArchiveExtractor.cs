using SharpCompress.Archives;

namespace GameShift.Windows.OptiScaler;

internal sealed class SharpCompressOptiScalerArchiveExtractor
    : IOptiScalerArchiveExtractor
{
    private const int MaximumFiles = 512;
    private const long MaximumExtractedBytes = 512L * 1024 * 1024;

    public async ValueTask ExtractAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        using IArchive archive = ArchiveFactory.Open(archivePath);
        int fileCount = 0;
        long totalBytes = 0;
        foreach (IArchiveEntry entry in archive.Entries.Where(entry => !entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            fileCount++;
            if (entry.Size < 0)
            {
                throw new InvalidDataException("Archiwum zawiera plik o nieznanym rozmiarze.");
            }

            totalBytes = checked(totalBytes + entry.Size);
            if (fileCount > MaximumFiles || totalBytes > MaximumExtractedBytes)
            {
                throw new InvalidDataException("Archiwum OptiScaler przekracza bezpieczne limity.");
            }

            string key = (entry.Key
                    ?? throw new InvalidDataException("Archiwum zawiera plik bez nazwy."))
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            string destination = GetContainedPath(destinationDirectory, key);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using Stream source = entry.OpenEntryStream();
            await using FileStream output = new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                written += read;
                if (written > entry.Size || written > MaximumExtractedBytes)
                {
                    throw new InvalidDataException(
                        "Wypakowany plik przekroczył zadeklarowany rozmiar.");
                }

                await output.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (written != entry.Size)
            {
                throw new InvalidDataException("Rozmiar wypakowanego pliku jest niezgodny.");
            }
        }

        if (fileCount == 0)
        {
            throw new InvalidDataException("Archiwum OptiScaler jest puste.");
        }
    }

    private static string GetContainedPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidDataException("Archiwum zawiera nieprawidłową ścieżkę.");
        }

        string fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Archiwum próbuje zapisać plik poza katalogiem roboczym.");
        }

        return candidate;
    }
}
