namespace GameShift.Windows.Power;

public interface IPowerSchemeAdapter
{
    ValueTask<Guid> GetActiveSchemeAsync(
        CancellationToken cancellationToken);

    ValueTask<bool> SchemeExistsAsync(
        Guid schemeId,
        CancellationToken cancellationToken);

    ValueTask DuplicateSchemeAsync(
        Guid sourceSchemeId,
        Guid destinationSchemeId,
        CancellationToken cancellationToken);

    ValueTask SetActiveSchemeAsync(
        Guid schemeId,
        CancellationToken cancellationToken);

    ValueTask DeleteSchemeAsync(
        Guid schemeId,
        CancellationToken cancellationToken);
}
