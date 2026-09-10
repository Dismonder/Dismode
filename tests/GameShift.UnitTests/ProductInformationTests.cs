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

        // Chodzi o to, ze wersja plynie z metadanych zespolu, a nie o to, jaka
        // akurat jest. Przypiecie dosownej wartosci zamienialo kazdy bump
        // wersji w recznaa poprawke tego testu — i wlasnie tak przeoczylismy
        // 0.5.1.
        Assert.AreEqual(
            expected: assemblyVersion.ToString(3),
            actual: ProductInformation.CurrentVersion);
        Assert.AreEqual(
            expected: $"GameShift {assemblyVersion.ToString(3)} Gaming Edition",
            actual: ProductInformation.FullDisplayName);
        Assert.IsNotNull(
            typeof(ProductInformation).GetProperty(
                nameof(ProductInformation.CurrentVersion)));
    }
}
