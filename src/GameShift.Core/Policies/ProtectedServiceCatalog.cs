namespace GameShift.Core.Policies;

public static class ProtectedServiceCatalog
{
    private static readonly Dictionary<string, TargetProtection> Protections =
            new Dictionary<string, TargetProtection>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["WinDefend"] = TargetProtection.WindowsSecurity,
                ["WdNisSvc"] = TargetProtection.WindowsSecurity,
                ["SecurityHealthService"] = TargetProtection.WindowsSecurity,
                ["Sense"] = TargetProtection.WindowsSecurity,
                ["mpssvc"] = TargetProtection.Firewall,
                ["BFE"] = TargetProtection.Firewall,
                ["SamSs"] = TargetProtection.Authentication,
                ["Netlogon"] = TargetProtection.Authentication,
                ["KeyIso"] =
                    TargetProtection.Authentication
                    | TargetProtection.CredentialStore,
                ["VaultSvc"] = TargetProtection.CredentialStore,
                ["RpcSs"] = TargetProtection.RemoteProcedureCall,
                ["DcomLaunch"] = TargetProtection.RemoteProcedureCall,
                ["RpcEptMapper"] = TargetProtection.RemoteProcedureCall,
                ["PlugPlay"] = TargetProtection.PlugAndPlay,
                ["DeviceInstall"] = TargetProtection.PlugAndPlay,
                ["Dhcp"] = TargetProtection.CoreNetworking,
                ["Dnscache"] = TargetProtection.CoreNetworking,
                ["NlaSvc"] = TargetProtection.CoreNetworking,
                ["nsi"] = TargetProtection.CoreNetworking,
                ["Netman"] = TargetProtection.CoreNetworking,
                ["Audiosrv"] = TargetProtection.Audio,
                ["AudioEndpointBuilder"] = TargetProtection.Audio,
                ["EFS"] = TargetProtection.Encryption,
                ["BDESVC"] = TargetProtection.Encryption,
                ["CryptSvc"] =
                    TargetProtection.Encryption
                    | TargetProtection.WindowsSecurity,
                ["EventLog"] = TargetProtection.UnknownIdentity,
                ["Schedule"] = TargetProtection.UnknownIdentity,
                ["Power"] = TargetProtection.UnknownIdentity,
                ["TrustedInstaller"] = TargetProtection.UnknownIdentity,
                ["wuauserv"] = TargetProtection.UnknownIdentity,
            };

    public static TargetProtection Classify(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        return Protections.TryGetValue(
            serviceName.Trim(),
            out TargetProtection protection)
            ? protection
            : TargetProtection.None;
    }
}
