using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Dismode.Windows.Security;

public static class NamedPipeSecurityFactory
{
    public static PipeSecurity Create(SecurityIdentifier allowedUserSid)
    {
        ArgumentNullException.ThrowIfNull(allowedUserSid);

        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        AddFullControl(security, allowedUserSid);
        AddFullControl(
            security,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(
            security,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        return security;
    }

    public static PipeSecurity CreateSystemService()
    {
        PipeSecurity security = new();
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);

        AddFullControl(
            security,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(
            security,
            new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null));
        security.AddAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(
                    WellKnownSidType.AuthenticatedUserSid,
                    null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
        return security;
    }

    private static void AddFullControl(
        PipeSecurity security,
        SecurityIdentifier identity)
    {
        PipeAccessRule rule = new(
            identity,
            PipeAccessRights.FullControl,
            AccessControlType.Allow);
        security.AddAccessRule(rule);
    }
}
