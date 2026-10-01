namespace Dismode.Windows.Profiles;

public sealed record GameArtwork(
    string LocalPath,
    GameArtworkSource Source)
{
    public Uri LocalUri => new(LocalPath);
}

public enum GameArtworkRole
{
    Poster,
    Hero,
}

public sealed record GameArtworkSet(
    GameArtwork? Poster,
    GameArtwork? Hero);

public enum GameArtworkSource
{
    SteamLibraryCache = 1,
    GameDirectory = 2,
    ExecutableThumbnail = 3,
    SteamOfficialCdn = 4,
    EpicCatalogCache = 5,
}
