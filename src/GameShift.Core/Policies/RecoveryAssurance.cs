namespace GameShift.Core.Policies;

public enum RecoveryAssurance
{
    None = 0,
    SnapshotCaptured = 1,
    CompensationDefined = 2,
    CompensationVerified = 3,
}

