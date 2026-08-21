namespace GameShift.Contracts.Protocol;

public static class PipeNames
{
    public const string System = "GameShift.System";

    public static string ForUser(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);

        if (userSid.Length > 184
            || userSid.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '.')))
        {
            throw new ArgumentException(
                "The user SID contains characters that are invalid in a GameShift pipe name.",
                nameof(userSid));
        }

        return $"GameShift.User.{userSid}";
    }
}

