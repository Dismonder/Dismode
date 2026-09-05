using System.Security.Cryptography;

namespace GameShift.Core.Updates;

public enum UpdateCheckState
{
    NotDue = 1,
    UpToDate = 2,
    Available = 3,
    Failed = 4,
    AheadOfChannel = 5,
    ManualUpgradeRequired = 6,
}

public sealed record UpdateVersionDecision(
    UpdateCheckState State,
    string Message,
    SignedUpdateManifest? Manifest);

public static class UpdateVersionPolicy
{
    public static UpdateVersionDecision Classify(
        string currentVersionText,
        SignedUpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Version currentVersion = UpdateManifestCodec.ParseVersion(
            currentVersionText,
            "installed version");
        Version offeredVersion = UpdateManifestCodec.ParseVersion(
            manifest.Version,
            "offered version");
        Version minimumSupportedVersion = UpdateManifestCodec.ParseVersion(
            manifest.MinimumSupportedVersion,
            "minimum supported version");
        string current = currentVersion.ToString(3);
        string offered = offeredVersion.ToString(3);

        if (offeredVersion < currentVersion)
        {
            return new(
                UpdateCheckState.AheadOfChannel,
                $"Zainstalowana wersja {current} jest nowsza niż kanał "
                    + $"{manifest.Channel} ({offered})",
                Manifest: null);
        }

        if (offeredVersion == currentVersion)
        {
            return new(
                UpdateCheckState.UpToDate,
                $"Masz aktualną wersję {current}.",
                Manifest: null);
        }

        if (currentVersion < minimumSupportedVersion)
        {
            return new(
                UpdateCheckState.ManualUpgradeRequired,
                $"Aby przejść z wersji {current} do {offered}, trzeba "
                    + "ręcznie przeinstalować GameShift. Minimalna wersja "
                    + "obsługiwana przez aktualizację automatyczną to "
                    + $"{minimumSupportedVersion.ToString(3)}.",
                Manifest: null);
        }

        return new(
            UpdateCheckState.Available,
            $"Dostępna jest wersja {offered}.",
            manifest);
    }
}

public static class UpdateFailurePolicy
{
    public static bool IsUserCancellation(
        Exception exception,
        CancellationToken requestedCancellation) =>
        exception is OperationCanceledException
            && requestedCancellation.IsCancellationRequested;

    public static bool IsHandled(Exception exception) =>
        exception is HttpRequestException
            or IOException
            or InvalidDataException
            or CryptographicException
            or OperationCanceledException;

    public static string Describe(Exception exception) =>
        exception switch
        {
            OperationCanceledException =>
                "Serwer aktualizacji nie odpowiedział w wymaganym czasie.",
            HttpRequestException =>
                "Nie można połączyć się z serwerem aktualizacji.",
            CryptographicException =>
                "Aktualizacja nie przeszła weryfikacji kryptograficznej.",
            InvalidDataException =>
                "Serwer zwrócił nieprawidłowy manifest aktualizacji.",
            _ => "Nie udało się sprawdzić aktualizacji.",
        };
}
