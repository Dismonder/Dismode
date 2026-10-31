using Dismode.MemoryOptimizer.Core.Ipc;

namespace Dismode.MemoryOptimizer.Presentation;

internal static class MemoryClientPolicy
{
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    internal static readonly TimeSpan OptimizeTimeout = TimeSpan.FromMinutes(2);

    // Connecting and status calls stay short; only a manual optimization,
    // which the service runs synchronously, may take long.
    internal static TimeSpan ExchangeTimeoutFor(
        MemoryOptimizerCommand command) =>
        command == MemoryOptimizerCommand.Optimize
            ? OptimizeTimeout
            : ConnectTimeout;

    internal static bool IsExpectedClientFailure(Exception exception) =>
        exception is IOException or InvalidOperationException or
            TimeoutException or OperationCanceledException or
            InvalidDataException or System.Text.Json.JsonException;
}
