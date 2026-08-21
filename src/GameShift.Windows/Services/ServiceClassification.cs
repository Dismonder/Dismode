namespace GameShift.Windows.Services;

public sealed record ServiceClassification(
    ServiceSafetyClassification Kind,
    string Reason,
    string RecommendedAction);
