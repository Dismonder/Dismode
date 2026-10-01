namespace Dismode.Core.History;

public interface ISessionHistoryRepository
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    ValueTask AddAsync(
        SessionSummary summary,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SessionSummary>> ListRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken);

    ValueTask<int> DeleteEndedBeforeAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken);
}
