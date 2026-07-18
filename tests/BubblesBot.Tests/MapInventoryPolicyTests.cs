using BubblesBot.Bot.Modes;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class MapInventoryPolicyTests
{
    [Fact]
    public void RetainsOnePortalStackAndCloisterSuppliesButDepositsMapsAndLoot()
    {
        InventoryView.Item[] items =
        [
            Item(10, "Metadata/Items/Currency/CurrencyPortal", 8),
            Item(20, "Metadata/Items/Currency/CurrencyPortal", 40),
            Item(30, "Metadata/Items/Scarabs/ScarabDivinationCardsNew1", 17),
            Item(40, "Metadata/Items/Maps/MapKeyTier16", 1),
            Item(50, "Metadata/Items/Currency/CurrencyRerollRare", 5),
        ];

        Assert.False(MapInventoryPolicy.ShouldRetainForNextRun(items, items[0]));
        Assert.True(MapInventoryPolicy.ShouldRetainForNextRun(items, items[1]));
        Assert.True(MapInventoryPolicy.ShouldRetainForNextRun(items, items[2]));
        Assert.False(MapInventoryPolicy.ShouldRetainForNextRun(items, items[3]));
        Assert.False(MapInventoryPolicy.ShouldRetainForNextRun(items, items[4]));
    }

    private static InventoryView.Item Item(long entity, string path, int stack)
        => new(0, (nint)entity, null, path, stack, 1, 1);
}
