using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Dismode.SystemAgent.Security;

/// <summary>
/// Files taken over from the GameShift directory arrive with the owner and
/// the explicit access entries they were created with, so whoever could write
/// them there could keep writing them here. Each one gets Administrators as
/// the owner and only what its directory passes down, which is what the
/// installer set up for the tree.
/// </summary>
internal static class MachineDataAccessControl
{
    /// <param name="root">
    /// Directory whose children are reset; its own entries, set by the
    /// installer, stay.
    /// </param>
    /// <param name="keep">
    /// Directories whose own entries stay as well; their children are reset.
    /// </param>
    public static IReadOnlyList<string> ResetChildrenToInherited(
        string root,
        params string[] keep)
    {
        List<string> problems = [];
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(
                root,
                "*",
                SearchOption.AllDirectories);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{root}: {exception.Message}");
            return problems;
        }

        string[] kept = keep.Select(Path.GetFullPath).ToArray();
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        foreach (string entry in entries)
        {
            if (kept.Contains(
                    Path.GetFullPath(entry),
                    StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(entry))
                {
                    DirectoryInfo directory = new(entry);
                    DirectorySecurity security = directory.GetAccessControl();
                    ResetToInherited(security, administrators);
                    directory.SetAccessControl(security);
                }
                else
                {
                    FileInfo file = new(entry);
                    FileSecurity security = file.GetAccessControl();
                    ResetToInherited(security, administrators);
                    file.SetAccessControl(security);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException
                    or Win32Exception)
            {
                problems.Add($"{entry}: {exception.Message}");
            }
        }

        return problems;
    }

    // A fresh security object would persist a null DACL (everyone, full
    // control); the existing one loses its explicit entries instead and
    // takes inheritance back from the directory.
    private static void ResetToInherited(
        FileSystemSecurity security,
        SecurityIdentifier owner)
    {
        foreach (FileSystemAccessRule rule in security
                     .GetAccessRules(
                         includeExplicit: true,
                         includeInherited: false,
                         typeof(SecurityIdentifier))
                     .Cast<FileSystemAccessRule>()
                     .ToArray())
        {
            security.RemoveAccessRuleAll(rule);
        }

        security.SetAccessRuleProtection(
            isProtected: false,
            preserveInheritance: false);
        security.SetOwner(owner);
    }
}
