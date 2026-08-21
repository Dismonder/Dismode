namespace GameShift.Windows.Services;

public interface IServiceInventory
{
    IReadOnlyList<ServiceSnapshot> Capture();
}

