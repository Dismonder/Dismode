namespace Dismode.Windows.Profiles;

public sealed record GameLibrarySyncResult(
    int DetectedCount,
    int AddedCount,
    int UpdatedCount,
    int ErrorCount);
