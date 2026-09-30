namespace Dismode.Windows.Processes;

public interface IProcessInventory
{
    IReadOnlyList<ProcessSnapshot> Capture();
}

