namespace GameShift.Windows.Processes;

public sealed record ProcessClassification(
    ProcessSafetyClassification Kind,
    string Reason,
    string RecommendedAction);
