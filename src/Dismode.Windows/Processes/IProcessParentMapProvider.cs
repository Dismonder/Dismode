namespace Dismode.Windows.Processes;

public interface IProcessParentMapProvider
{
    IReadOnlyDictionary<int, int> Capture();
}
