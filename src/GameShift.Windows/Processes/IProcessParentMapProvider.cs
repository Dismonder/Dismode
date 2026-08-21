namespace GameShift.Windows.Processes;

public interface IProcessParentMapProvider
{
    IReadOnlyDictionary<int, int> Capture();
}
