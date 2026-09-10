namespace GameShift.Data.Storage;

public static class GameShiftStoragePaths
{
    public static string MachineDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GameShift");

    public static string UserDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameShift");

    public static string UserDatabasePath =>
        Path.Combine(UserDataDirectory, "gameshift-user.db");

    public static string UserRecoveryJournalPath =>
        Path.Combine(UserDataDirectory, "user-recovery.jsonl");

    public static string UserUpdatesDirectory =>
        Path.Combine(UserDataDirectory, "Updates");

    /// <summary>
    /// Where OptiScaler installation manifests live. Each file records what
    /// was written into one game directory and what has to be put back, so
    /// their presence means files of ours are sitting inside somebody's game.
    /// </summary>
    public static string OptiScalerInstallationsDirectory =>
        Path.Combine(UserDataDirectory, "OptiScaler", "Installations");

    public static string MachineRecoveryJournalPath =>
        Path.Combine(MachineDataDirectory, "machine-recovery.jsonl");

    public static string SystemOptimizerDatabasePath =>
        Path.Combine(MachineDataDirectory, "system-optimizer.db");

    public static string TrustedSignerConfigurationPath =>
        Path.Combine(MachineDataDirectory, "trusted-signers.json");
}
