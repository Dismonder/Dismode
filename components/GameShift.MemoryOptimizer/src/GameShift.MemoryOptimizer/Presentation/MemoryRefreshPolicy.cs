namespace GameShift.MemoryOptimizer.Presentation;

internal sealed class MemoryRefreshPolicy
{
    private bool _statusInProgress;
    private bool _historyInProgress;
    private int _historyGeneration;
    private int _completedHistoryGeneration;
    private int _requestedHistoryGeneration;

    public string ActivePage { get; private set; } = "overview";

    public bool IsVisible { get; set; } = true;

    public bool IsCompact { get; set; }

    public bool ShouldRefreshProcesses =>
        IsVisible && !IsCompact && ActivePage == "processes";

    public bool NeedsHistory =>
        IsVisible && !IsCompact && ActivePage == "history" &&
        !_historyInProgress && _completedHistoryGeneration != _historyGeneration;

    public void SelectPage(string page)
    {
        ActivePage = page;
        if (page == "history")
        {
            InvalidateHistory();
        }
    }

    public void InvalidateHistory() => _historyGeneration++;

    public bool TryBeginStatus()
    {
        if (_statusInProgress)
        {
            return false;
        }

        _statusInProgress = true;
        return true;
    }

    public void EndStatus() => _statusInProgress = false;

    public bool TryBeginHistory()
    {
        if (!NeedsHistory)
        {
            return false;
        }

        _historyInProgress = true;
        _requestedHistoryGeneration = _historyGeneration;
        return true;
    }

    public void EndHistory(bool success)
    {
        if (success)
        {
            _completedHistoryGeneration = _requestedHistoryGeneration;
        }

        _historyInProgress = false;
    }
}

internal sealed class MemoryOperationGate
{
    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public bool TryBegin(bool serviceBusy) =>
        !serviceBusy && Interlocked.CompareExchange(ref _running, 1, 0) == 0;

    public void End() => Interlocked.Exchange(ref _running, 0);
}
