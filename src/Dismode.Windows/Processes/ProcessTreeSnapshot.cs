using Dismode.Core.Domain.Processes;

namespace Dismode.Windows.Processes;

public sealed record ProcessTreeSnapshot(
    ProcessIdentity Root,
    IReadOnlyList<ProcessTreeMember> Members,
    DateTimeOffset CapturedAtUtc);
