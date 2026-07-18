using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>Inventory retention for repeat map-farming hideout preflight.</summary>
public static class MapInventoryPolicy
{
    public static bool ShouldRetainForNextRun(
        IReadOnlyList<InventoryView.Item> inventory,
        in InventoryView.Item candidate)
    {
        if (StackedDeckPolicy.IsCloisterScarab(candidate.Path)) return true;
        if (!IsPortalScroll(candidate)) return false;

        InventoryView.Item? retained = null;
        foreach (var item in inventory)
        {
            if (!IsPortalScroll(item)) continue;
            if (retained is null
                || item.StackSize > retained.Value.StackSize
                || item.StackSize == retained.Value.StackSize
                    && (long)item.ItemEntity < (long)retained.Value.ItemEntity)
                retained = item;
        }
        return retained is { } keep && keep.ItemEntity == candidate.ItemEntity;
    }

    private static bool IsPortalScroll(in InventoryView.Item item)
        => item.Path.Contains(
            InventoryView.PortalScrollPathFragment,
            StringComparison.OrdinalIgnoreCase);
}
