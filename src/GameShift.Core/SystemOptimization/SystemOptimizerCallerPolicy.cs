namespace GameShift.Core.SystemOptimization;

public sealed class SystemOptimizerCallerPolicy
{
    private static readonly HashSet<string> ReadableExecutables = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "GameShift.exe",
        "GameShift.UI.exe",
        "GameShift.SessionHost.exe",
        "GameShift.SystemOptimizer.exe",
    };

    private static readonly HashSet<string> MutatingExecutables = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "GameShift.SessionHost.exe",
        "GameShift.SystemOptimizer.exe",
    };

    private readonly string _installationRoot;
    private readonly string _installationPrefix;

    public SystemOptimizerCallerPolicy(string installationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationRoot);
        _installationRoot = Path.GetFullPath(installationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _installationPrefix = _installationRoot + Path.DirectorySeparatorChar;
    }

    public SystemOptimizerCallerAuthorization Authorize(
        SystemOptimizerCallerIdentity caller,
        string claimedUserSid,
        bool requiresMutation)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimedUserSid);

        if (caller.ProcessId <= 0)
        {
            return Denied("Windows did not provide a valid caller process ID.");
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(
                caller.ActualUserSid,
                claimedUserSid.Trim()))
        {
            return Denied(
                "The claimed SID does not match the impersonated pipe identity.");
        }

        string executablePath;
        try
        {
            executablePath = Path.GetFullPath(caller.ExecutablePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            return Denied("The caller executable path is invalid.");
        }

        string? parent = Path.GetDirectoryName(executablePath);
        if (parent is null
            || (!StringComparer.OrdinalIgnoreCase.Equals(parent, _installationRoot)
                && !executablePath.StartsWith(
                    _installationPrefix,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return Denied(
                "The caller does not run from the GameShift installation directory.");
        }

        string executableName = Path.GetFileName(executablePath);
        if (!ReadableExecutables.Contains(executableName))
        {
            return Denied("The caller executable is not an allowed GameShift component.");
        }

        bool trustedMutationClient =
            MutatingExecutables.Contains(executableName)
            && caller.HasValidAuthenticodeSignature
            && caller.IsTrustedSigner;
        if (requiresMutation && !trustedMutationClient)
        {
            return new(
                IsAuthorized: false,
                IsReadOnly: true,
                "System mutations require a trusted Authenticode signature on SessionHost or SystemOptimizer.");
        }

        return new(
            IsAuthorized: true,
            IsReadOnly: !trustedMutationClient,
            trustedMutationClient
                ? "Trusted GameShift mutation client."
                : "Unsigned or untrusted build: read-only access only.");
    }

    private static SystemOptimizerCallerAuthorization Denied(string reason) =>
        new(IsAuthorized: false, IsReadOnly: true, reason);
}

public sealed record SystemOptimizerCallerIdentity(
    string ActualUserSid,
    int ProcessId,
    string ExecutablePath,
    bool HasValidAuthenticodeSignature,
    bool IsTrustedSigner);

public sealed record SystemOptimizerCallerAuthorization(
    bool IsAuthorized,
    bool IsReadOnly,
    string Reason);
