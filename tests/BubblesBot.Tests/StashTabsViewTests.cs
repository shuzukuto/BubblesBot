using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class StashTabsViewTests
{
    [Fact]
    public void Find_DepositPrefersGeneralPurposeDuplicate()
    {
        var tabs = new StashTabsView(
        [
            new StashTabsView.Tab("Dump", Type: 16, DisplayIndex: 17),
            new StashTabsView.Tab("Dump", Type: 7, DisplayIndex: 0),
            new StashTabsView.Tab("Deli", Type: 15, DisplayIndex: 8),
        ]);

        var dump = tabs.Find("dump", requireGeneralPurpose: true);

        Assert.NotNull(dump);
        Assert.Equal(0, dump.DisplayIndex);
        Assert.Equal((uint)7, dump.Type);
    }

    [Fact]
    public void Find_SupplyAllowsSpecializedTab()
    {
        var tabs = new StashTabsView(
        [
            new StashTabsView.Tab("Dump", Type: 7, DisplayIndex: 0),
            new StashTabsView.Tab("Deli", Type: 15, DisplayIndex: 8),
        ]);

        var deli = tabs.Find("DELI", requireGeneralPurpose: false);

        Assert.NotNull(deli);
        Assert.Equal(8, deli.DisplayIndex);
    }
}
