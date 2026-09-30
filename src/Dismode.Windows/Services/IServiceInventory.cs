namespace Dismode.Windows.Services;

public interface IServiceInventory
{
    IReadOnlyList<ServiceSnapshot> Capture();
}

