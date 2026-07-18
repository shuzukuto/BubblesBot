using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class InventoryMapSelectionTests
{
    [Theory]
    [InlineData("Metadata/Items/Maps/MapKeyTier16", true)]
    [InlineData("metadata/items/maps/mapkeytier16", true)]
    [InlineData("Metadata/Items/Currency/CurrencyAfflictionFragment", false)]
    [InlineData("Metadata/Items/Currency/CurrencyPortal", false)]
    public void MapKeyMetadataIsRecognized(string path, bool expected)
    {
        var item = new InventoryView.Item(0, 0, null, path, 1, 1, 1);

        Assert.Equal(expected, InventoryView.IsMap(item));
    }

    [Fact]
    public void MapsAreRetainedAsRunSupplies()
    {
        var item = new InventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1);

        Assert.True(InventoryView.IsRetainedSupply(item));
    }

    [Fact]
    public void UberBlightedStatPositivelyIdentifiesBlightRavagedMap()
    {
        var item = new InventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1,
            [(InventoryView.UberBlightedMapStatId, 1)]);

        Assert.True(InventoryView.IsBlightRavagedMap(item));
    }

    [Fact]
    public void StashItemUsesSamePositiveBlightRavagedIdentity()
    {
        var item = new StashInventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1,
            [(InventoryView.UberBlightedMapStatId, 1)]);

        Assert.True(StashInventoryView.IsBlightRavagedMap(item));
    }

    [Fact]
    public void StashMapWithoutSubtypeStatIsRejected()
    {
        var item = new StashInventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1);

        Assert.False(StashInventoryView.IsBlightRavagedMap(item));
    }

    [Theory]
    [InlineData("Metadata/Items/Maps/MapKeyTier16", 0)]
    [InlineData("Metadata/Items/Maps/MapKeyTier16", -1)]
    [InlineData("Metadata/Items/Currency/CurrencyPortal", 1)]
    public void MissingFalseOrNonMapStatIsRejected(string path, int value)
    {
        var item = new InventoryView.Item(
            0, 0, null, path, 1, 1, 1,
            [(InventoryView.UberBlightedMapStatId, value)]);

        Assert.False(InventoryView.IsBlightRavagedMap(item));
    }
}
