using System.Security.Principal;

namespace Dismode.Windows.Security;

public static class CurrentWindowsIdentity
{
    public static SecurityIdentifier GetUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User
            ?? throw new InvalidOperationException(
                "The current Windows identity does not expose a user SID.");
    }
}

