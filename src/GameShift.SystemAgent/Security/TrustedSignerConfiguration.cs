using System.Text.Json;
using GameShift.Data.Storage;

namespace GameShift.SystemAgent.Security;

internal static class TrustedSignerConfiguration
{
    private const int MaximumConfigurationBytes = 64 * 1024;

    internal static IReadOnlyList<string> Load()
    {
        List<string> thumbprints = [];
        string path = GameShiftStoragePaths.TrustedSignerConfigurationPath;
        try
        {
            FileInfo file = new(path);
            if (file.Exists && file.Length <= MaximumConfigurationBytes)
            {
                byte[] payload = File.ReadAllBytes(path);
                TrustedSignerDocument? document =
                    JsonSerializer.Deserialize<TrustedSignerDocument>(payload);
                if (document?.SchemaVersion == 1)
                {
                    thumbprints.AddRange(
                        document.Thumbprints.Where(IsThumbprint));
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException)
        {
        }

        if (StringComparer.Ordinal.Equals(
                Environment.GetEnvironmentVariable(
                    "GAMESHIFT_ALLOW_TEST_CERTIFICATE"),
                "1"))
        {
            string? testThumbprint = Environment.GetEnvironmentVariable(
                "GAMESHIFT_TEST_SIGNER_THUMBPRINT");
            if (testThumbprint is not null && IsThumbprint(testThumbprint))
            {
                thumbprints.Add(testThumbprint);
            }
        }

        return thumbprints
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsThumbprint(string value) =>
        value.Length == 40 && value.All(char.IsAsciiHexDigit);

    private sealed record TrustedSignerDocument(
        int SchemaVersion,
        IReadOnlyList<string> Thumbprints);
}
