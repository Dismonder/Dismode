using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Dismode.Data.Storage;

namespace Dismode.SystemAgent.Security;

internal static class TrustedSignerConfiguration
{
    private const int MaximumConfigurationBytes = 64 * 1024;

    // Build-LocalRelease.ps1 writes camelCase keys.
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web);

    internal static IReadOnlyList<string> Load()
    {
        List<string> thumbprints = [];
        string path = DismodeStoragePaths.TrustedSignerConfigurationPath;
        try
        {
            FileInfo file = new(path);
            if (file.Exists
                && file.Length <= MaximumConfigurationBytes
                && IsOwnedByAdministratorsOrSystem(file))
            {
                byte[] payload = File.ReadAllBytes(path);
                thumbprints.AddRange(ParseThumbprints(payload));
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
                    "DISMODE_ALLOW_TEST_CERTIFICATE"),
                "1"))
        {
            string? testThumbprint = Environment.GetEnvironmentVariable(
                "DISMODE_TEST_SIGNER_THUMBPRINT");
            if (testThumbprint is not null && IsThumbprint(testThumbprint))
            {
                thumbprints.Add(testThumbprint);
            }
        }

        return thumbprints
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ParseThumbprints(byte[] payload)
    {
        try
        {
            TrustedSignerDocument? document =
                JsonSerializer.Deserialize<TrustedSignerDocument>(
                    payload,
                    SerializerOptions);
            return document?.SchemaVersion == 1 && document.Thumbprints is not null
                ? document.Thumbprints
                    .OfType<string>()
                    .Where(IsThumbprint)
                    .ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The file names who may mutate the system through this service, so it
    /// is trusted only when it belongs to Administrators or SYSTEM — which
    /// is what an elevated installer creates and what Install-SystemAgent.ps1
    /// sets explicitly. A copy planted by an ordinary user — possible while
    /// the data directory still carried the ProgramData default of letting
    /// everyone create files — is ignored, and the service stays read-only.
    /// </summary>
    private static bool IsOwnedByAdministratorsOrSystem(FileInfo file)
    {
        try
        {
            IdentityReference? owner = file
                .GetAccessControl(AccessControlSections.Owner)
                .GetOwner(typeof(SecurityIdentifier));
            return owner is SecurityIdentifier sid
                && (sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                    || sid.IsWellKnown(WellKnownSidType.LocalSystemSid));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsThumbprint(string value) =>
        value.Length == 40 && value.All(char.IsAsciiHexDigit);

    private sealed record TrustedSignerDocument(
        int SchemaVersion,
        IReadOnlyList<string?>? Thumbprints);
}
