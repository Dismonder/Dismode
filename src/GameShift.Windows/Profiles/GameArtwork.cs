namespace GameShift.Windows.Profiles;

public sealed record GameArtwork(
    string LocalPath,
    GameArtworkSource Source)
{
    public Uri LocalUri => new(LocalPath);
}

public enum GameArtworkSource
{
    SteamLibraryCache = 1,
    GameDirectory = 2,
    ExecutableThumbnail = 3,
}
