using GameShift.Core.Domain.Processes;

namespace GameShift.Windows.Processes;

public sealed record ProcessTreeSnapshot(
    ProcessIdentity Root,
    IReadOnlyList<ProcessTreeMember> Members,
    DateTimeOffset CapturedAtUtc);
