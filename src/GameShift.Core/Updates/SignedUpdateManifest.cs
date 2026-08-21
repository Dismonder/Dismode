using System.Text.Json.Serialization;

namespace GameShift.Core.Updates;

public sealed record UpdatePackageChunk(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record UpdatePackageDescriptor(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("chunks")]
    IReadOnlyList<UpdatePackageChunk> Chunks);

public sealed record SignedUpdateManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("minimumSupportedVersion")]
    string MinimumSupportedVersion,
    [property: JsonPropertyName("publishedAtUtc")]
    DateTimeOffset PublishedAtUtc,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("releaseNotes")]
    IReadOnlyList<string> ReleaseNotes,
    [property: JsonPropertyName("installer")]
    UpdatePackageDescriptor Installer,
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("signatureAlgorithm")]
    string SignatureAlgorithm,
    [property: JsonPropertyName("signature")] string Signature);
