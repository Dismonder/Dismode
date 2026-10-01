namespace Dismode.Core.Product;

public static class ProductInformation
{
    public const string DisplayName = "Dismode";
    public static string CurrentVersion => typeof(ProductInformation)
        .Assembly
        .GetName()
        .Version?
        .ToString(3)
        ?? throw new InvalidOperationException(
            "Dismode.Core assembly version is unavailable.");
    public static string FullDisplayName =>
        $"{DisplayName} {CurrentVersion} Gaming Edition";
    public const string Publisher = "Dismode Research Project";
    public const string Copyright =
        "Copyright © 2026 Dismode Research Project";
    public const string UpdateServiceBaseUri =
        "https://dismode-update-service-dev.dismonder.workers.dev/";
    public const string TrustedUpdateKeyId =
        "dismode-development-2026-01";
    public const string TrustedUpdatePublicKeySubjectPublicKeyInfoBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEWGWiWgCSMEVY8rJGZAn5Qa5lBGZiV90PZtlsr6tIvoFxx15lk/TV/ywPKca1Wqu89GIcFvHpPzpy5Bcu7+Fd1Q==";
    public const string SafetyStatement = "No system change is allowed without a verified recovery path.";
}
