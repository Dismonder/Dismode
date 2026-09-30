using Dismode.Core.Domain.Processes;

namespace Dismode.Windows.Processes;

public sealed record ProcessTreeMember(
    ProcessIdentity Identity,
    int? ParentProcessId,
    int Depth);
