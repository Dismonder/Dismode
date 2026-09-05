namespace GameShift.MemoryOptimizer.Presentation;

internal sealed class MemoryFeedbackState
{
    private bool _isServiceConnectionError;

    public string? Message { get; private set; }

    public void ShowError(string message, bool isServiceConnectionError)
    {
        Message = message;
        _isServiceConnectionError = isServiceConnectionError;
    }

    public bool ClearRecoveredServiceError()
    {
        if (!_isServiceConnectionError)
        {
            return false;
        }

        Clear();
        return true;
    }

    public void Clear()
    {
        Message = null;
        _isServiceConnectionError = false;
    }
}
