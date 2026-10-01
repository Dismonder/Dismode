namespace Dismode.Core.Policies;

[Flags]
public enum TargetProtection : ulong
{
    None = 0,
    WindowsSecurity = 1UL << 0,
    Firewall = 1UL << 1,
    Authentication = 1UL << 2,
    RemoteProcedureCall = 1UL << 3,
    PlugAndPlay = 1UL << 4,
    CoreNetworking = 1UL << 5,
    Audio = 1UL << 6,
    FileSystem = 1UL << 7,
    CredentialStore = 1UL << 8,
    Encryption = 1UL << 9,
    Driver = 1UL << 10,
    KernelService = 1UL << 11,
    ActiveDeviceDependency = 1UL << 12,
    AntiCheat = 1UL << 13,
    ActiveGameDependency = 1UL << 14,
    UnknownIdentity = 1UL << 15,
}

