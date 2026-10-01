using Dismode.Core.SystemOptimization;

namespace Dismode.UnitTests.SystemOptimization;

[TestClass]
public sealed class SystemOptimizerCallerPolicyTests
{
    private const string Sid = "S-1-5-21-1000";
    private const string InstallRoot = @"C:\Program Files\Dismode";

    [TestMethod]
    public void UnsignedInstalledClientGetsReadOnlyAccessOnly()
    {
        SystemOptimizerCallerPolicy policy = new(InstallRoot);
        SystemOptimizerCallerIdentity caller = new(
            Sid,
            ProcessId: 1234,
            Path.Combine(InstallRoot, "Dismode.SystemOptimizer.exe"),
            HasValidAuthenticodeSignature: false,
            IsTrustedSigner: false);

        SystemOptimizerCallerAuthorization read = policy.Authorize(
            caller,
            Sid,
            requiresMutation: false);
        SystemOptimizerCallerAuthorization write = policy.Authorize(
            caller,
            Sid,
            requiresMutation: true);

        Assert.IsTrue(read.IsAuthorized);
        Assert.IsTrue(read.IsReadOnly);
        Assert.IsFalse(write.IsAuthorized);
        Assert.IsTrue(write.IsReadOnly);
    }

    [TestMethod]
    public void MutationRequiresActualSidInstalledPathAllowedBinaryAndTrustedSigner()
    {
        SystemOptimizerCallerPolicy policy = new(InstallRoot);
        SystemOptimizerCallerIdentity valid = new(
            Sid,
            ProcessId: 1234,
            Path.Combine(InstallRoot, "Dismode.SessionHost.exe"),
            HasValidAuthenticodeSignature: true,
            IsTrustedSigner: true);

        Assert.IsTrue(policy.Authorize(valid, Sid, requiresMutation: true).IsAuthorized);
        Assert.IsFalse(policy.Authorize(
            valid with { ActualUserSid = "S-1-5-21-2000" },
            Sid,
            requiresMutation: true).IsAuthorized);
        Assert.IsFalse(policy.Authorize(
            valid with { ExecutablePath = @"C:\Temp\Dismode.SessionHost.exe" },
            Sid,
            requiresMutation: true).IsAuthorized);
        Assert.IsFalse(policy.Authorize(
            valid with { ExecutablePath = Path.Combine(InstallRoot, "powershell.exe") },
            Sid,
            requiresMutation: true).IsAuthorized);
    }

    [TestMethod]
    public void PrefixCollisionCannotEscapeInstallationRoot()
    {
        SystemOptimizerCallerPolicy policy = new(InstallRoot);
        SystemOptimizerCallerIdentity collision = new(
            Sid,
            ProcessId: 42,
            @"C:\Program Files\Dismode-Evil\Dismode.SystemOptimizer.exe",
            HasValidAuthenticodeSignature: true,
            IsTrustedSigner: true);

        SystemOptimizerCallerAuthorization result = policy.Authorize(
            collision,
            Sid,
            requiresMutation: false);

        Assert.IsFalse(result.IsAuthorized);
    }
}
