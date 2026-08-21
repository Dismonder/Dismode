using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GameShift.Core.Updates;

public static partial class UpdateManifestCodec
{
    public const int CurrentSchemaVersion = 1;
    public const string EcdsaP256Sha256Algorithm =
        "ECDSA_P256_SHA256_P1363";
    public const long MaximumChunkSizeBytes = 25L * 1024L * 1024L;
    public const long MaximumInstallerSizeBytes = 2L * 1024L * 1024L * 1024L;
    public const int MaximumChunkCount = 128;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static SignedUpdateManifest DeserializeAndVerify(
        ReadOnlySpan<byte> json,
        Uri updateServiceBaseUri,
        string expectedChannel,
        string trustedKeyId,
        ReadOnlySpan<byte> publicKeySubjectPublicKeyInfo)
    {
        SignedUpdateManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SignedUpdateManifest>(
                    json,
                    SerializerOptions)
                ?? throw new InvalidDataException(
                    "The update manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The update manifest JSON is invalid.",
                exception);
        }

        Validate(
            manifest,
            updateServiceBaseUri,
            expectedChannel,
            trustedKeyId);

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(manifest.Signature);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "The update manifest signature is not valid Base64.",
                exception);
        }

        if (signature.Length != 64)
        {
            throw new InvalidDataException(
                "The update manifest signature has an invalid length.");
        }

        using ECDsa verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(
            publicKeySubjectPublicKeyInfo,
            out int bytesRead);
        if (bytesRead != publicKeySubjectPublicKeyInfo.Length)
        {
            throw new InvalidDataException(
                "The trusted update public key contains trailing data.");
        }

        byte[] canonicalPayload = CreateCanonicalPayload(manifest);
        bool isValid = verifier.VerifyData(
            canonicalPayload,
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (!isValid)
        {
            throw new CryptographicException(
                "The update manifest signature is invalid.");
        }

        return manifest;
    }

    public static SignedUpdateManifest Sign(
        SignedUpdateManifest manifest,
        Uri updateServiceBaseUri,
        ECDsa signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        Validate(
            manifest with { Signature = Convert.ToBase64String(new byte[64]) },
            updateServiceBaseUri,
            manifest.Channel,
            manifest.KeyId);
        byte[] signature = signingKey.SignData(
            CreateCanonicalPayload(manifest),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return manifest with { Signature = Convert.ToBase64String(signature) };
    }

    public static byte[] Serialize(SignedUpdateManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);

    public static byte[] CreateCanonicalPayload(
        SignedUpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions
        {
            Indented = false,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
            writer.WriteString("channel", manifest.Channel);
            writer.WriteString("version", manifest.Version);
            writer.WriteString(
                "minimumSupportedVersion",
                manifest.MinimumSupportedVersion);
            writer.WriteString(
                "publishedAtUtc",
                manifest.PublishedAtUtc.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture));
            writer.WriteString("displayName", manifest.DisplayName);
            writer.WriteStartArray("releaseNotes");
            foreach (string note in manifest.ReleaseNotes)
            {
                writer.WriteStringValue(note);
            }

            writer.WriteEndArray();
            writer.WriteStartObject("installer");
            writer.WriteString("fileName", manifest.Installer.FileName);
            writer.WriteNumber("sizeBytes", manifest.Installer.SizeBytes);
            writer.WriteString("sha256", manifest.Installer.Sha256);
            writer.WriteStartArray("chunks");
            foreach (UpdatePackageChunk chunk in manifest.Installer.Chunks)
            {
                writer.WriteStartObject();
                writer.WriteString("path", chunk.Path);
                writer.WriteNumber("sizeBytes", chunk.SizeBytes);
                writer.WriteString("sha256", chunk.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteString("keyId", manifest.KeyId);
            writer.WriteString(
                "signatureAlgorithm",
                manifest.SignatureAlgorithm);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static void Validate(
        SignedUpdateManifest manifest,
        Uri updateServiceBaseUri,
        string expectedChannel,
        string trustedKeyId)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(updateServiceBaseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedChannel);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedKeyId);

        if (!updateServiceBaseUri.IsAbsoluteUri
            || !string.Equals(
                updateServiceBaseUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The update service base URI must use HTTPS.");
        }

        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "The update manifest schema is unsupported.");
        }

        if (!string.Equals(
                manifest.Channel,
                expectedChannel,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The update manifest channel does not match the requested channel.");
        }

        Version version = ParseVersion(manifest.Version, "version");
        Version minimumVersion = ParseVersion(
            manifest.MinimumSupportedVersion,
            "minimum supported version");
        if (minimumVersion > version)
        {
            throw new InvalidDataException(
                "The minimum supported version exceeds the offered version.");
        }

        if (manifest.PublishedAtUtc > DateTimeOffset.UtcNow.AddDays(1))
        {
            throw new InvalidDataException(
                "The update manifest publication time is too far in the future.");
        }

        if (string.IsNullOrWhiteSpace(manifest.DisplayName)
            || manifest.DisplayName.Length > 160)
        {
            throw new InvalidDataException(
                "The update display name is invalid.");
        }

        if (manifest.ReleaseNotes is null
            || manifest.ReleaseNotes.Count > 20
            || manifest.ReleaseNotes.Any(note =>
                string.IsNullOrWhiteSpace(note) || note.Length > 500))
        {
            throw new InvalidDataException(
                "The update release notes are invalid.");
        }

        if (!string.Equals(
                manifest.KeyId,
                trustedKeyId,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.SignatureAlgorithm,
                EcdsaP256Sha256Algorithm,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The update signing key or algorithm is not trusted.");
        }

        ValidateInstaller(manifest, version, updateServiceBaseUri);
        if (string.IsNullOrWhiteSpace(manifest.Signature))
        {
            throw new InvalidDataException(
                "The update manifest signature is missing.");
        }
    }

    public static Version ParseVersion(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !ThreePartVersionRegex().IsMatch(value)
            || !Version.TryParse(value, out Version? parsed))
        {
            throw new InvalidDataException(
                $"The update {fieldName} is invalid.");
        }

        return parsed;
    }

    private static void ValidateInstaller(
        SignedUpdateManifest manifest,
        Version version,
        Uri updateServiceBaseUri)
    {
        UpdatePackageDescriptor installer = manifest.Installer
            ?? throw new InvalidDataException(
                "The update installer descriptor is missing.");
        string normalizedVersion = version.ToString(3);
        string expectedFileName =
            $"GameShift-Setup-{normalizedVersion}-win-x64.exe";
        if (!string.Equals(
                installer.FileName,
                expectedFileName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The update installer file name is invalid.");
        }

        if (installer.SizeBytes <= 0
            || installer.SizeBytes > MaximumInstallerSizeBytes
            || !IsSha256(installer.Sha256))
        {
            throw new InvalidDataException(
                "The update installer size or SHA-256 is invalid.");
        }

        if (installer.Chunks is null
            || installer.Chunks.Count is < 1 or > MaximumChunkCount)
        {
            throw new InvalidDataException(
                "The update installer chunk list is invalid.");
        }

        HashSet<string> uniquePaths = new(StringComparer.Ordinal);
        long totalSize = 0;
        for (int index = 0; index < installer.Chunks.Count; index++)
        {
            UpdatePackageChunk chunk = installer.Chunks[index]
                ?? throw new InvalidDataException(
                    "The update installer contains an empty chunk.");
            string expectedPath =
                $"/v1/packages/{normalizedVersion}/"
                + $"GameShift-Setup-{normalizedVersion}-win-x64.part-"
                + $"{index + 1:0000}.bin";
            if (!string.Equals(chunk.Path, expectedPath, StringComparison.Ordinal)
                || !uniquePaths.Add(chunk.Path)
                || chunk.SizeBytes <= 0
                || chunk.SizeBytes > MaximumChunkSizeBytes
                || !IsSha256(chunk.Sha256))
            {
                throw new InvalidDataException(
                    "The update installer chunk descriptor is invalid.");
            }

            Uri chunkUri = new(updateServiceBaseUri, chunk.Path);
            if (!string.Equals(
                    chunkUri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    chunkUri.Host,
                    updateServiceBaseUri.Host,
                    StringComparison.OrdinalIgnoreCase)
                || chunkUri.Port != updateServiceBaseUri.Port)
            {
                throw new InvalidDataException(
                    "The update chunk path leaves the trusted update origin.");
            }

            checked
            {
                totalSize += chunk.SizeBytes;
            }
        }

        if (totalSize != installer.SizeBytes)
        {
            throw new InvalidDataException(
                "The update chunk sizes do not match the installer size.");
        }
    }

    private static bool IsSha256(string value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F');

    [GeneratedRegex(
        "^[0-9]+\\.[0-9]+\\.[0-9]+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ThreePartVersionRegex();
}
