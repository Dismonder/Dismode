using Dismode.Contracts.SystemOptimization;

namespace Dismode.Core.SystemOptimization;

public interface IHardwareFingerprintProvider
{
    HardwareFingerprint Capture();
}
