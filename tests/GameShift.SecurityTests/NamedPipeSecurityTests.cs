using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using GameShift.Windows.Processes;
using GameShift.Windows.Security;

namespace GameShift.SecurityTests;

[TestClass]
public sealed class NamedPipeSecurityTests
{
    [TestMethod]
    public void CreateUsesProtectedAllowListWithoutBroadPrincipals()
    {
        SecurityIdentifier currentUser = CurrentWindowsIdentity.GetUserSid();
        SecurityIdentifier localSystem = new(
            WellKnownSidType.LocalSystemSid,
            null);
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        SecurityIdentifier everyone = new(
            WellKnownSidType.WorldSid,
            null);
        SecurityIdentifier authenticatedUsers = new(
            WellKnownSidType.AuthenticatedUserSid,
            null);

        PipeSecurity security = NamedPipeSecurityFactory.Create(currentUser);
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier));

        SecurityIdentifier[] allowedSids = rules
            .Cast<PipeAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .ToArray();

        Assert.IsTrue(security.AreAccessRulesProtected);
        CollectionAssert.AreEquivalent(
            new[] { currentUser, localSystem, administrators },
            allowedSids);
        CollectionAssert.DoesNotContain(allowedSids, everyone);
        CollectionAssert.DoesNotContain(allowedSids, authenticatedUsers);
    }

    [TestMethod]
    public void CreateGrantsFullControlOnlyThroughExplicitAllowRules()
    {
        PipeSecurity security = NamedPipeSecurityFactory.Create(
            CurrentWindowsIdentity.GetUserSid());
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier));

        foreach (PipeAccessRule rule in rules.Cast<PipeAccessRule>())
        {
            Assert.AreEqual(AccessControlType.Allow, rule.AccessControlType);
            Assert.AreEqual(PipeAccessRights.FullControl, rule.PipeAccessRights);
            Assert.IsFalse(rule.IsInherited);
        }
    }

    [TestMethod]
    public void SystemServicePipeAllowsAuthenticatedClientsButNotAnonymousOrEveryone()
    {
        SecurityIdentifier localSystem = new(
            WellKnownSidType.LocalSystemSid,
            null);
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        SecurityIdentifier authenticatedUsers = new(
            WellKnownSidType.AuthenticatedUserSid,
            null);
        SecurityIdentifier everyone = new(
            WellKnownSidType.WorldSid,
            null);
        SecurityIdentifier anonymous = new(
            WellKnownSidType.AnonymousSid,
            null);

        PipeSecurity security = NamedPipeSecurityFactory.CreateSystemService();
        PipeAccessRule[] rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();
        SecurityIdentifier[] identities = rules
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .ToArray();

        Assert.IsTrue(security.AreAccessRulesProtected);
        CollectionAssert.Contains(identities, localSystem);
        CollectionAssert.Contains(identities, administrators);
        CollectionAssert.Contains(identities, authenticatedUsers);
        CollectionAssert.DoesNotContain(identities, everyone);
        CollectionAssert.DoesNotContain(identities, anonymous);
        PipeAccessRule authenticatedRule = rules.Single(rule =>
            Equals(rule.IdentityReference, authenticatedUsers));
        Assert.AreEqual(
            PipeAccessRights.ReadWrite,
            authenticatedRule.PipeAccessRights & PipeAccessRights.ReadWrite);
    }

    [TestMethod]
    public void NativeInteropLoadsLibrariesOnlyFromSystem32()
    {
        DefaultDllImportSearchPathsAttribute? attribute =
            typeof(ProcessIdentityProvider)
                .Assembly
                .GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();

        Assert.IsNotNull(attribute);
        Assert.AreEqual(DllImportSearchPath.System32, attribute.Paths);
    }
}
