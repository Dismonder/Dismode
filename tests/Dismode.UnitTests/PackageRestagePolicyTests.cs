using Dismode.UpdatePublisher;

namespace Dismode.UnitTests;

[TestClass]
public sealed class PackageRestagePolicyTests
{
    [TestMethod]
    public void AllowsByteIdenticalRestage()
    {
        using TempPackages packages = new(
            existing: [1, 2, 3],
            staged: [1, 2, 3]);

        PackageRestagePolicy.EnsureIdenticalPackage(
            packages.Existing,
            packages.Staged);
    }

    [TestMethod]
    public void RefusesDifferentChunkBytes()
    {
        using TempPackages packages = new(
            existing: [1, 2, 3],
            staged: [1, 2, 4]);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            PackageRestagePolicy.EnsureIdenticalPackage(
                packages.Existing,
                packages.Staged));
    }

    [TestMethod]
    public void RefusesDifferentChunkSet()
    {
        using TempPackages packages = new(
            existing: [1, 2, 3],
            staged: [1, 2, 3]);
        File.WriteAllBytes(
            Path.Combine(packages.Staged, "part-0002.bin"),
            [9]);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            PackageRestagePolicy.EnsureIdenticalPackage(
                packages.Existing,
                packages.Staged));
    }

    private sealed class TempPackages : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "dismode-restage-" + Guid.NewGuid().ToString("N"));

        public TempPackages(byte[] existing, byte[] staged)
        {
            Existing = Path.Combine(_root, "existing");
            Staged = Path.Combine(_root, "staged");
            Directory.CreateDirectory(Existing);
            Directory.CreateDirectory(Staged);
            File.WriteAllBytes(Path.Combine(Existing, "part-0001.bin"), existing);
            File.WriteAllBytes(Path.Combine(Staged, "part-0001.bin"), staged);
        }

        public string Existing { get; }

        public string Staged { get; }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
