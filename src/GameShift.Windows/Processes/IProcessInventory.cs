namespace GameShift.Windows.Processes;

public interface IProcessInventory
{
    IReadOnlyList<ProcessSnapshot> Capture();
}

