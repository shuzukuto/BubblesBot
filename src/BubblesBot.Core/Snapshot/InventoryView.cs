using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Player inventory panel view — resolves <c>IngameUi → InventoryPanel → PlayerInventory</c>
/// and projects each visible item to a clickable window-relative rect plus its metadata path
/// and stack size. Lightweight: built lazily off the snapshot, reads are bounded by the
/// (small) visible-item count.
///
/// <para>Two consumers in the stacked-deck loop: (1) the deposit step needs each item's screen
/// rect to Ctrl+click it into the open stash tab, and (2) telemetry / retain logic needs to
/// count Portal Scrolls (the F-key exit's fuel). Note item fields only populate while the
/// inventory panel is actually open — a closed inventory returns <see cref="IsOpen"/> false
/// and an empty list. Opening the stash also opens the inventory, so the deposit flow reads
/// items reliably once the stash is up.</para>
/// </summary>
public sealed class InventoryView
{
    /// <summary>Portal Scroll metadata path fragment (<c>Metadata/Items/Currency/CurrencyPortal</c>).</summary>
    public const string PortalScrollPathFragment = "CurrencyPortal";
    public const string SimulacrumPathFragment = "CurrencyAfflictionFragment";
    public const string MapPathFragment = "/Maps/MapKey";
    // Stats.dat key: is_uber_blighted_map. Live-correlated against the
    // UberInfectedMap__ raw mod on 2026-07-15.
    public const int UberBlightedMapStatId = 14927;

    public readonly record struct Item(
        nint ElementAddress,
        nint ItemEntity,
        ElementGeometry.Rect? Rect,
        string Path,
        int StackSize,
        int Width,
        int Height,
        IReadOnlyList<(int Id, int Value)>? Stats = null)
    {
        public int OccupiedCells => Math.Max(1, Width) * Math.Max(1, Height);
    }

    /// <summary>True when the inventory panel pointer resolves (inventory is open).</summary>
    public bool IsOpen { get; }
    public IReadOnlyList<Item> Items { get; }
    public int OccupiedCells => Items.Sum(x => x.OccupiedCells);

    private InventoryView(bool isOpen, IReadOnlyList<Item> items)
    {
        IsOpen = isOpen;
        Items = items;
    }

    public static InventoryView FromIngameUi(MemoryReader reader, nint ingameStateAddress)
    {
        if (!reader.TryReadStruct<nint>(ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ingameUi) || ingameUi == 0)
            return new InventoryView(false, Array.Empty<Item>());
        if (!reader.TryReadStruct<nint>(ingameUi + KnownOffsets.IngameUiElements.InventoryPanel, out var panel) || panel == 0)
            return new InventoryView(false, Array.Empty<Item>());
        if (!ElementReader.IsVisibleDeep(reader, panel))
            return new InventoryView(false, Array.Empty<Item>());
        if (!InventoryReader.TryGetPlayerInventory(reader, panel, out var inv))
            return new InventoryView(true, Array.Empty<Item>());

        var snap = InventoryReader.TryReadInventory(reader, inv);
        if (snap is null)
            return new InventoryView(true, Array.Empty<Item>());

        var items = new List<Item>(snap.VisibleItems.Count);
        foreach (var vi in snap.VisibleItems)
        {
            var rect  = ElementGeometry.TryReadRect(reader, vi.Address);
            var path  = EntityListReader.ReadEntityPath(reader, vi.ItemEntity) ?? string.Empty;
            var stack = ReadStackSize(reader, vi.ItemEntity);
            var stats = ItemStatsReader.Read(reader, vi.ItemEntity);
            items.Add(new Item(vi.Address, vi.ItemEntity, rect, path, stack, vi.Width, vi.Height, stats));
        }
        return new InventoryView(true, items);
    }

    /// <summary>Total number of Portal Scrolls in the inventory (sum of stack sizes).</summary>
    public int PortalScrollCount()
    {
        var total = 0;
        foreach (var it in Items)
            if (it.Path.Contains(PortalScrollPathFragment, StringComparison.OrdinalIgnoreCase))
                total += Math.Max(1, it.StackSize);
        return total;
    }

    /// <summary>True iff this item is a mapping supply the loop must never deposit (v1: Portal Scrolls).</summary>
    public static bool IsRetainedSupply(in Item item)
        => item.Path.Contains(PortalScrollPathFragment, StringComparison.OrdinalIgnoreCase)
        || item.Path.Contains(SimulacrumPathFragment, StringComparison.OrdinalIgnoreCase)
        || IsMap(item);

    /// <summary>
    /// True for a normal map-item payload. Blighted and Blight-ravaged maps retain the
    /// ordinary MapKey metadata path; their subtype lives on the Mods component.
    /// Callers that cannot decode raw mod names must fail closed when more than one map is
    /// visible rather than choosing an arbitrary candidate.
    /// </summary>
    public static bool IsMap(in Item item)
        => item.Path.Contains(MapPathFragment, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True only when the item is a map and its flattened Mods stats positively expose
    /// <c>is_uber_blighted_map</c>. This lets automation select from several carried maps
    /// without consuming a normal map whose metadata path is otherwise identical.
    /// </summary>
    public static bool IsBlightRavagedMap(in Item item)
        => IsMap(item)
        && item.Stats is not null
        && item.Stats.Any(stat => stat.Id == UberBlightedMapStatId && stat.Value > 0);

    private static int ReadStackSize(MemoryReader reader, nint itemEntity)
    {
        var comps = EntityComponents.ReadComponentMap(reader, itemEntity);
        if (comps.TryGetValue("Stack", out var stackAddr)
            && reader.TryReadStruct<int>(stackAddr + KnownOffsets.StackComponent.CurrentCount, out var count)
            && count > 0 && count < 100_000)
            return count;
        return 1;
    }
}
