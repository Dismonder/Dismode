using System.Collections.Concurrent;
using System.Diagnostics;

namespace Dismode.MemoryService;

internal sealed class MemoryServiceRuntime : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, UserEndpoint> _users = new(
        StringComparer.OrdinalIgnoreCase);
    private Task? _discoveryTask;

    internal void Start()
    {
        if (_discoveryTask is not null)
        {
            throw new InvalidOperationException("Memory service is already running.");
        }

        _discoveryTask = DiscoverUsersAsync(_lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_discoveryTask is not null)
        {
            try
            {
                await _discoveryTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                _lifetime.IsCancellationRequested)
            {
            }
        }

        foreach ((string userSid, UserEndpoint endpoint) in _users)
        {
            if (_users.TryRemove(userSid, out UserEndpoint? removed))
            {
                await DisposeEndpointAsync(
                    userSid,
                    removed).ConfigureAwait(false);
            }
        }

        _lifetime.Dispose();
    }

    private async Task DiscoverUsersAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HashSet<string> interactiveUsers = new(
                InteractiveUserProvider.GetInteractiveUserSids(),
                StringComparer.OrdinalIgnoreCase);
            foreach ((string userSid, UserEndpoint endpoint) in _users)
            {
                if (interactiveUsers.Contains(userSid) && endpoint.IsHealthy)
                {
                    continue;
                }

                if (_users.TryRemove(userSid, out UserEndpoint? removed))
                {
                    await DisposeEndpointAsync(
                        userSid,
                        removed).ConfigureAwait(false);
                }
            }

            foreach (string userSid in interactiveUsers)
            {
                if (_users.ContainsKey(userSid))
                {
                    continue;
                }

                MemoryUserRuntime runtime = new(userSid);
                try
                {
                    await runtime.InitializeAsync(
                        cancellationToken).ConfigureAwait(false);
                    UserEndpoint endpoint = new(userSid, runtime, cancellationToken);
                    if (!_users.TryAdd(userSid, endpoint))
                    {
                        await endpoint.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException)
                {
                    await runtime.DisposeAsync().ConfigureAwait(false);
                    Trace.TraceError(
                        "Memory Optimizer endpoint for {0} could not start: {1}",
                        userSid,
                        exception.Message);
                }
            }

            await Task.Delay(
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DisposeEndpointAsync(
        string userSid,
        UserEndpoint endpoint)
    {
        try
        {
            await endpoint.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            Trace.TraceError(
                "Memory Optimizer endpoint for {0} stopped with an error: {1}",
                userSid,
                exception.Message);
        }
    }

    private sealed class UserEndpoint : IAsyncDisposable
    {
        private readonly MemoryUserRuntime _runtime;
        private readonly CancellationTokenSource _lifetime;
        private readonly Task _pipeTask;
        private readonly Task _automationTask;

        internal UserEndpoint(
            string userSid,
            MemoryUserRuntime runtime,
            CancellationToken serviceCancellationToken)
        {
            _runtime = runtime;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                serviceCancellationToken);
            _pipeTask = new MemoryPipeServer(userSid, runtime).RunAsync(
                _lifetime.Token);
            _automationTask = runtime.RunAutomationAsync(_lifetime.Token);
        }

        internal bool IsHealthy =>
            !_pipeTask.IsCompleted && !_automationTask.IsCompleted;

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            try
            {
                try
                {
                    await Task.WhenAll(
                        _pipeTask,
                        _automationTask).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    _lifetime.IsCancellationRequested)
                {
                }
            }
            finally
            {
                _lifetime.Dispose();
                await _runtime.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
