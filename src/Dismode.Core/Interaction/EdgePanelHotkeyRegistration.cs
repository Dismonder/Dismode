namespace Dismode.Core.Interaction;

public sealed class EdgePanelHotkeyRegistration(Func<bool> register, Action unregister) : IDisposable
{
    private readonly Func<bool> _register = register ?? throw new ArgumentNullException(nameof(register));
    private readonly Action _unregister = unregister ?? throw new ArgumentNullException(nameof(unregister));
    public bool IsRegistered { get; private set; }

    public bool TryRegister() => IsRegistered || (IsRegistered = _register());

    public void Release()
    {
        if (IsRegistered)
        {
            _unregister();
            IsRegistered = false;
        }
    }

    public void Dispose() => Release();
}
