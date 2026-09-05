using GameShift.Contracts.SystemOptimization;

namespace GameShift.Core.SystemOptimization;

public interface IHardwareFingerprintProvider
{
    HardwareFingerprint Capture();
}
