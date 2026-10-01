namespace Dismode.MemoryOptimizer.Core;

public static class MemoryOptimizerProduct
{
    public static string Version =>
        typeof(MemoryOptimizerProduct).Assembly.GetName().Version?.ToString(3) ??
        "unknown";

    public static string FileVersion =>
        typeof(MemoryOptimizerProduct).Assembly.GetName().Version?.ToString() ??
        "unknown";

    public static string DisplayName =>
        $"Dismode Memory Optimizer {Version}";
}
