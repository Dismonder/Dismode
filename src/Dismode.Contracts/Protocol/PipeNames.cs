namespace Dismode.Contracts.Protocol;

public static class PipeNames
{
    public const string System = "Dismode.System";

    public static string ForUser(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);

        if (userSid.Length > 184
            || userSid.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '.')))
        {
            throw new ArgumentException(
                "The user SID contains characters that are invalid in a Dismode pipe name.",
                nameof(userSid));
        }

        return $"Dismode.User.{userSid}";
    }
}

