using GameShift.Core.Product;

namespace GameShift.UnitTests;

[TestClass]
public sealed class ProductInformationTests
{
    [TestMethod]
    public void ProductVersionAndNameComeFromCoreAssemblyMetadata()
    {
        Version assemblyVersion = typeof(ProductInformation)
            .Assembly
            .GetName()
            .Version!;

        Assert.AreEqual(
            expected: assemblyVersion.ToString(3),
            actual: ProductInformation.CurrentVersion);
        Assert.AreEqual(
            expected: "0.4.7",
            actual: assemblyVersion.ToString(3));
        Assert.AreEqual(
            expected: "GameShift 0.4.7 Gaming Edition",
            actual: ProductInformation.FullDisplayName);
        Assert.IsNotNull(
            typeof(ProductInformation).GetProperty(
                nameof(ProductInformation.CurrentVersion)));
    }
}
