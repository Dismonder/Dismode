using GameShift.Core.Domain.Processes;

namespace GameShift.Windows.Processes;

public sealed record ProcessTreeMember(
    ProcessIdentity Identity,
    int? ParentProcessId,
    int Depth);
