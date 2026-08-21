using System.Security.Cryptography;
using System.Text.Json;
using GameShift.Core.Product;
using GameShift.Core.Updates;

const string defaultKeyName =
    "GameShift Development Update Signing 2026-01";
const int chunkSizeBytes = 20 * 1024 * 1024;

try
{
    if (args.Length == 0)
    {
        PrintUsage();
        return 2;
    }

    return args[0].ToLowerInvariant() switch
    {
        "key-info" => PrintKeyInfo(),
        "stage" => await StageReleaseAsync(args[1..]),
        _ => throw new ArgumentException(
            $"Unknown command: {args[0]}."),
    };
}
catch (Exception exception) when (
    exception is ArgumentException
        or IOException
        or UnauthorizedAccessException
        or CryptographicException
        or InvalidDataException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static int PrintKeyInfo()
{
    using ECDsaCng signer = OpenOrCreateDevelopmentSigningKey();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        keyId = ProductInformation.TrustedUpdateKeyId,
        keyName = defaultKeyName,
        publicKeySubjectPublicKeyInfoBase64 = Convert.ToBase64String(
            signer.ExportSubjectPublicKeyInfo()),
        privateKeyExportable = false,
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

static async Task<int> StageReleaseAsync(string[] arguments)
{
    string installerPath = RequireOption(arguments, "--installer");
    string publicAssetsPath = RequireOption(arguments, "--assets");
    string versionText = RequireOption(arguments, "--version");
    string channel = ReadOption(arguments, "--channel") ?? "preview";
    string minimumVersionText =
        ReadOption(arguments, "--minimum-version") ?? versionText;
    string baseUriText = ReadOption(arguments, "--base-uri")
        ?? ProductInformation.UpdateServiceBaseUri;
    string[] releaseNotes = ReadRepeatedOptions(arguments, "--note");
    if (releaseNotes.Length == 0)
    {
        releaseNotes = ["Aktualizacja techniczna GameShift."];
    }

    Version version = UpdateManifestCodec.ParseVersion(
        versionText,
        "version");
    _ = UpdateManifestCodec.ParseVersion(
        minimumVersionText,
        "minimum supported version");
    if (channel is not ("preview" or "stable"))
    {
        throw new ArgumentException(
            "--channel must be preview or stable.");
    }

    if (!Uri.TryCreate(baseUriText, UriKind.Absolute, out Uri? baseUri)
        || baseUri.Scheme != Uri.UriSchemeHttps)
    {
        throw new ArgumentException(
            "--base-uri must be an absolute HTTPS URI.");
    }

    string fullInstallerPath = Path.GetFullPath(installerPath);
    string fullAssetsPath = Path.GetFullPath(publicAssetsPath);
    if (!File.Exists(fullInstallerPath))
    {
        throw new FileNotFoundException(
            "The installer does not exist.",
            fullInstallerPath);
    }

    Directory.CreateDirectory(fullAssetsPath);
    FileInfo installer = new(fullInstallerPath);
    string normalizedVersion = version.ToString(3);
    string expectedInstallerName =
        $"GameShift-Setup-{normalizedVersion}-win-x64.exe";
    if (!string.Equals(
            installer.Name,
            expectedInstallerName,
            StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"The installer must be named {expectedInstallerName}.");
    }

    if (installer.Length <= 0
        || installer.Length > UpdateManifestCodec.MaximumInstallerSizeBytes)
    {
        throw new InvalidDataException(
            "The installer size is outside the supported range.");
    }

    string packagesRoot = EnsureChildPath(fullAssetsPath, "v1", "packages");
    string channelsRoot = EnsureChildPath(fullAssetsPath, "v1", "channels");
    string packageTarget = EnsureChildPath(packagesRoot, normalizedVersion);
    string packageTemporary = EnsureChildPath(
        packagesRoot,
        $".{normalizedVersion}-{Guid.NewGuid():N}.tmp");
    Directory.CreateDirectory(packageTemporary);

    try
    {
        List<UpdatePackageChunk> chunks = await SplitInstallerAsync(
            fullInstallerPath,
            packageTemporary,
            normalizedVersion);
        string installerSha256 = await ComputeSha256Async(
            fullInstallerPath);
        SignedUpdateManifest unsignedManifest = new(
            SchemaVersion: UpdateManifestCodec.CurrentSchemaVersion,
            Channel: channel,
            Version: normalizedVersion,
            MinimumSupportedVersion: minimumVersionText,
            PublishedAtUtc: DateTimeOffset.UtcNow,
            DisplayName:
                $"GameShift {normalizedVersion} Technical Preview",
            ReleaseNotes: releaseNotes,
            Installer: new(
                FileName: expectedInstallerName,
                SizeBytes: installer.Length,
                Sha256: installerSha256,
                Chunks: chunks),
            KeyId: ProductInformation.TrustedUpdateKeyId,
            SignatureAlgorithm:
                UpdateManifestCodec.EcdsaP256Sha256Algorithm,
            Signature: string.Empty);

        using ECDsaCng signer = OpenOrCreateDevelopmentSigningKey();
        SignedUpdateManifest signedManifest = UpdateManifestCodec.Sign(
            unsignedManifest,
            baseUri,
            signer);
        byte[] manifestBytes = UpdateManifestCodec.Serialize(signedManifest);
        _ = UpdateManifestCodec.DeserializeAndVerify(
            manifestBytes,
            baseUri,
            channel,
            ProductInformation.TrustedUpdateKeyId,
            signer.ExportSubjectPublicKeyInfo());

        if (Directory.Exists(packageTarget))
        {
            ValidateGeneratedPackageDirectory(
                packageTarget,
                packagesRoot,
                normalizedVersion);
            Directory.Delete(packageTarget, recursive: true);
        }

        Directory.Move(packageTemporary, packageTarget);
        string channelDirectory = EnsureChildPath(channelsRoot, channel);
        Directory.CreateDirectory(channelDirectory);
        string manifestPath = EnsureChildPath(
            channelDirectory,
            "manifest.json");
        string manifestTemporary = manifestPath + ".tmp";
        await File.WriteAllBytesAsync(manifestTemporary, manifestBytes);
        File.Move(manifestTemporary, manifestPath, overwrite: true);

        Console.WriteLine($"Manifest: {manifestPath}");
        Console.WriteLine($"Package chunks: {chunks.Count}");
        Console.WriteLine($"Installer SHA-256: {installerSha256}");
        Console.WriteLine(
            "The immutable package was staged before the channel manifest.");
        return 0;
    }
    finally
    {
        if (Directory.Exists(packageTemporary))
        {
            Directory.Delete(packageTemporary, recursive: true);
        }
    }
}

static async Task<List<UpdatePackageChunk>> SplitInstallerAsync(
    string installerPath,
    string outputDirectory,
    string version)
{
    await using FileStream input = new(
        installerPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 1024 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    byte[] buffer = new byte[chunkSizeBytes];
    List<UpdatePackageChunk> chunks = [];
    int chunkNumber = 1;
    while (input.Position < input.Length)
    {
        int bytesRead = 0;
        while (bytesRead < buffer.Length && input.Position < input.Length)
        {
            int read = await input.ReadAsync(
                buffer.AsMemory(bytesRead, buffer.Length - bytesRead));
            if (read == 0)
            {
                break;
            }

            bytesRead += read;
        }

        if (bytesRead == 0)
        {
            break;
        }

        string chunkName =
            $"GameShift-Setup-{version}-win-x64.part-{chunkNumber:0000}.bin";
        string chunkPath = Path.Combine(outputDirectory, chunkName);
        await using (FileStream output = new(
            chunkPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await output.WriteAsync(buffer.AsMemory(0, bytesRead));
            await output.FlushAsync();
            output.Flush(flushToDisk: true);
        }

        chunks.Add(new(
            $"/v1/packages/{version}/{chunkName}",
            bytesRead,
            Convert.ToHexString(SHA256.HashData(
                buffer.AsSpan(0, bytesRead)))));
        chunkNumber++;
    }

    if (chunks.Count == 0
        || chunks.Count > UpdateManifestCodec.MaximumChunkCount)
    {
        throw new InvalidDataException(
            "The installer produced an invalid number of chunks.");
    }

    return chunks;
}

static async Task<string> ComputeSha256Async(string filePath)
{
    await using FileStream stream = new(
        filePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 1024 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    byte[] hash = await SHA256.HashDataAsync(stream);
    return Convert.ToHexString(hash);
}

static ECDsaCng OpenOrCreateDevelopmentSigningKey()
{
    CngProvider provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;
    CngKey key;
    if (CngKey.Exists(defaultKeyName, provider))
    {
        key = CngKey.Open(defaultKeyName, provider);
    }
    else
    {
        CngKeyCreationParameters parameters = new()
        {
            Provider = provider,
            KeyUsage = CngKeyUsages.Signing,
            ExportPolicy = CngExportPolicies.None,
        };
        key = CngKey.Create(
            CngAlgorithm.ECDsaP256,
            defaultKeyName,
            parameters);
    }

    return new ECDsaCng(key);
}

static string EnsureChildPath(string parent, params string[] components)
{
    string fullParent = Path.GetFullPath(parent)
        .TrimEnd(Path.DirectorySeparatorChar)
        + Path.DirectorySeparatorChar;
    string candidate = Path.GetFullPath(
        Path.Combine([fullParent, .. components]));
    if (!candidate.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            "A generated update path escaped the public assets directory.");
    }

    return candidate;
}

static void ValidateGeneratedPackageDirectory(
    string target,
    string packagesRoot,
    string version)
{
    string expected = EnsureChildPath(packagesRoot, version);
    if (!string.Equals(
            Path.GetFullPath(target),
            expected,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            "Refusing to replace an unexpected package directory.");
    }
}

static string RequireOption(string[] arguments, string name) =>
    ReadOption(arguments, name)
    ?? throw new ArgumentException($"Missing required option {name}.");

static string? ReadOption(string[] arguments, string name)
{
    for (int index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(
                arguments[index],
                name,
                StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }

    return null;
}

static string[] ReadRepeatedOptions(string[] arguments, string name)
{
    List<string> values = [];
    for (int index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(
                arguments[index],
                name,
                StringComparison.OrdinalIgnoreCase))
        {
            values.Add(arguments[index + 1]);
            index++;
        }
    }

    return [.. values];
}

static void PrintUsage()
{
    Console.WriteLine(
        "GameShift.UpdatePublisher key-info\n"
        + "GameShift.UpdatePublisher stage --installer <exe> "
        + "--assets <public-dir> --version <x.y.z> "
        + "[--channel preview|stable] [--minimum-version <x.y.z>] "
        + "[--base-uri <https-url>] [--note <text>]...");
}
