using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Dismode.UI.Services;

internal static class LocalArtworkImageLoader
{
    private const ulong MaximumArtworkBytes = 32UL * 1024 * 1024;
    private static readonly HashSet<string> SupportedExtensions = new(
        [".bmp", ".gif", ".ico", ".jpeg", ".jpg", ".png", ".webp"],
        StringComparer.OrdinalIgnoreCase);

    internal static async ValueTask<ImageSource?> LoadAsync(
        string? artworkPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupportedLocalPath(artworkPath))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(artworkPath);
            StorageFile file = await StorageFile
                .GetFileFromPathAsync(fullPath)
                .AsTask(cancellationToken);
            ulong size = (await file
                    .GetBasicPropertiesAsync()
                    .AsTask(cancellationToken))
                .Size;
            if (size is 0 or > MaximumArtworkBytes)
            {
                return null;
            }

            using IRandomAccessStreamWithContentType stream =
                await file.OpenReadAsync().AsTask(cancellationToken);
            BitmapImage image = new();
            await image.SetSourceAsync(stream).AsTask(cancellationToken);
            return image;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or COMException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSupportedLocalPath(
        [NotNullWhen(true)] string? artworkPath)
    {
        if (string.IsNullOrWhiteSpace(artworkPath)
            || !Path.IsPathFullyQualified(artworkPath)
            || artworkPath.StartsWith(@"\\", StringComparison.Ordinal)
            || !SupportedExtensions.Contains(
                Path.GetExtension(artworkPath)))
        {
            return false;
        }

        return !Uri.TryCreate(
                artworkPath,
                UriKind.Absolute,
                out Uri? artworkUri)
            || artworkUri.IsFile && !artworkUri.IsUnc;
    }
}
