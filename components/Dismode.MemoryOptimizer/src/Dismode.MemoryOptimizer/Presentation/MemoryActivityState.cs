// SPDX-License-Identifier: GPL-3.0-only
namespace Dismode.MemoryOptimizer.Presentation;

internal readonly record struct MemoryActivityState(bool RamUsage, bool Optimize, bool Save, bool Compact)
{
    public static MemoryActivityState Create(
        bool visible, bool compact, bool busy, bool saving, bool animationsEnabled) => new(
            visible && !compact,
            visible && !compact && busy && animationsEnabled,
            visible && !compact && saving && animationsEnabled,
            visible && compact && (busy || saving) && animationsEnabled);
}
