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

    public static string MachineRecoveryJournalPath =>
        Path.Combine(MachineDataDirectory, "machine-recovery.jsonl");

    public static string SystemOptimizerDatabasePath =>
        Path.Combine(MachineDataDirectory, "system-optimizer.db");

    public static string TrustedSignerConfigurationPath =>
        Path.Combine(MachineDataDirectory, "trusted-signers.json");
}
