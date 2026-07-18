using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Mechanics;

public sealed class SimulacrumStateProbe : IProbe
{
    public string Name => "mechanic.simulacrum-state";
    public string Group => "mechanic";
    public string Description => "Capture the Simulacrum Afflictionator monolith and its raw StateMachine array for phase diffing.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx) => MechanicStateCapture.Capture(ctx,
        "Simulacrum",
        snapshot => Contains(snapshot, "Objects/Afflictionator"));

    public ProbeResult Discover(ProbeContext ctx) => Validate(ctx);

    private static bool Contains(EntityListReader.EntitySnapshot snapshot, string fragment)
        => snapshot.Path.Contains(fragment, StringComparison.OrdinalIgnoreCase)
        || snapshot.Metadata.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}

public sealed class BlightTowerStateProbe : IProbe
{
    public string Name => "mechanic.blight-tower-state";
    public string Group => "mechanic";
    public string Description => "Capture Blight foundations/towers and StateMachine arrays for build postconditions.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx) => MechanicStateCapture.Capture(ctx,
        "Blight tower",
        snapshot => snapshot.Path.Contains("BlightFoundation", StringComparison.OrdinalIgnoreCase)
                 || snapshot.Path.Contains("BlightTower", StringComparison.OrdinalIgnoreCase)
                 || snapshot.Metadata.Contains("BlightFoundation", StringComparison.OrdinalIgnoreCase)
                 || snapshot.Metadata.Contains("BlightTower", StringComparison.OrdinalIgnoreCase));

    public ProbeResult Discover(ProbeContext ctx) => Validate(ctx);
}

public sealed class BlightStateProbe : IProbe
{
    public string Name => "mechanic.blight-state";
    public string Group => "mechanic";
    public string Description => "Capture the complete Blight encounter contract: pump, lanes, towers, chests, portal labels, countdown, and skip UI.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var entities = MechanicStateCapture.Capture(ctx,
            "Blight",
            snapshot => snapshot.Path.Contains("Blight", StringComparison.OrdinalIgnoreCase)
                     || snapshot.Metadata.Contains("Blight", StringComparison.OrdinalIgnoreCase)
                     || snapshot.Path.Contains("MultiplexPortal", StringComparison.OrdinalIgnoreCase)
                     || snapshot.Kind == EntityListReader.EntityKind.TownPortal);

        var snapshot = new GameSnapshot(
            ctx.Reader, ctx.Chain.IngameData, ctx.Chain.IngameState,
            new WindowInfo(0, 0, 1920, 1080));
        var skip = snapshot.BlightSkipButton;
        var countdown = snapshot.BlightCountdown;
        var labels = snapshot.GroundLabels
            .Where(label => label.Path.Contains("Blight", StringComparison.OrdinalIgnoreCase)
                         || label.Path.Contains("Portal", StringComparison.OrdinalIgnoreCase))
            .Take(128)
            .Select(label =>
            {
                var rect = label.LabelRect;
                var rectText = rect is { } r
                    ? $"{r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0}"
                    : "none";
                var grid = label.EntityGridPosition is { } g ? $"{g.X},{g.Y}" : "unknown";
                return $"id={label.EntityId} grid={grid} visible={label.IsLabelVisible} "
                     + $"onScreen={label.IsRectOnScreen} rect={rectText} path={label.Path}";
            })
            .ToArray();
        var skipRect = skip.ClickRect is { } sr
            ? $"{sr.X:F0},{sr.Y:F0},{sr.Width:F0},{sr.Height:F0}"
            : "none";
        var uiEvidence = $"Blight UI: countdown='{countdown ?? "<null>"}' "
                       + $"timerDone={snapshot.IsBlightTimerDone} skipVisible={skip.IsVisible} "
                       + $"skipRect={skipRect} currency={snapshot.BlightCurrency.Currency?.ToString() ?? "unknown"} "
                       + $"currencyRaw='{snapshot.BlightCurrency.RawText}' labels={labels.Length}";
        var blightTexts = CaptureBlightPanelTexts(ctx);
        var evidence = uiEvidence
                     + (blightTexts.Length == 0 ? string.Empty : Environment.NewLine + "Blight panel texts: " + string.Join(" | ", blightTexts))
                     + (labels.Length == 0 ? string.Empty : Environment.NewLine + string.Join(Environment.NewLine, labels))
                     + Environment.NewLine + entities.Message;
        return new ProbeResult(entities.Status, evidence, entities.Candidates);
    }

    public ProbeResult Discover(ProbeContext ctx) => Validate(ctx);

    private static string[] CaptureBlightPanelTexts(ProbeContext ctx)
    {
        if (!ctx.Reader.TryReadStruct<nint>(
                ctx.Chain.IngameState + KnownOffsets.IngameState.IngameUi, out var ui)
            || ui == 0)
            return [];
        ctx.Reader.TryReadStruct<nint>(
            ui + KnownOffsets.IngameUiElements.BlightEncounterUi, out var panel);
        ctx.Reader.TryReadStruct<nint>(ui + KnownOffsets.Element.Parent, out var parent);
        var encounterRoot = parent;
        if (encounterRoot != 0
            && ElementReader.TryGetChild(ctx.Reader, encounterRoot, 1, out var c1)
            && ElementReader.TryGetChild(ctx.Reader, c1, 25, out var c25))
            encounterRoot = c25;
        var result = new List<string>();
        var queue = new Queue<(nint Address, string Path, int Depth)>();
        var seen = new HashSet<nint>();
        if (panel != 0) queue.Enqueue((panel, "panel", 0));
        if (encounterRoot != 0) queue.Enqueue((encounterRoot, "parent/1/25", 0));
        var uiSnapshot = ElementReader.TryReadSnapshot(ctx.Reader, ui, 256);
        if (uiSnapshot is not null)
        {
            for (var i = 0; i < Math.Min(40, uiSnapshot.Children.Count); i++)
            {
                var top = uiSnapshot.Children[i];
                if (!ElementReader.TryGetChild(ctx.Reader, top, 0, out var inner)
                    || !ElementReader.TryGetChild(ctx.Reader, inner, 3, out var hud)) continue;
                var durability = ResolvePath(ctx.Reader, hud, 1, 0, 0);
                var currency = ResolvePath(ctx.Reader, hud, 2, 0, 1);
                var durabilityText = ReadElementText(ctx.Reader, durability);
                var currencyText = ReadElementText(ctx.Reader, currency);
                if (ElementReader.IsVisibleDeep(ctx.Reader, hud)
                    || !string.IsNullOrWhiteSpace(durabilityText)
                    || !string.IsNullOrWhiteSpace(currencyText))
                    result.Add($"candidate[{i}] hudVisible={ElementReader.IsVisibleDeep(ctx.Reader, hud)} durability='{durabilityText}' currency='{currencyText}'");
            }
        }
        while (queue.Count > 0 && seen.Count < 1000)
        {
            var (address, path, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(ctx.Reader, address, 256);
            if (element is null) continue;
            var text = NativeString.Read(ctx.Reader, address + KnownOffsets.Element.TextNoTags);
            if (string.IsNullOrWhiteSpace(text))
                text = NativeString.Read(ctx.Reader, address + KnownOffsets.Element.Text);
            if (!string.IsNullOrWhiteSpace(text))
                result.Add($"{path}='{text}' visible={ElementReader.IsVisibleDeep(ctx.Reader, address)}");
            if (depth >= 10) continue;
            for (var i = 0; i < element.Children.Count; i++)
                queue.Enqueue((element.Children[i], $"{path}/{i}", depth + 1));
        }
        return result.Take(100).ToArray();
    }

    private static nint ResolvePath(MemoryReader reader, nint root, params int[] path)
    {
        var current = root;
        foreach (var index in path)
            if (!ElementReader.TryGetChild(reader, current, index, out current)) return 0;
        return current;
    }

    private static string ReadElementText(MemoryReader reader, nint address)
    {
        if (address == 0) return string.Empty;
        var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
        return string.IsNullOrWhiteSpace(text)
            ? NativeString.Read(reader, address + KnownOffsets.Element.Text)
            : text;
    }
}

public sealed class ShrineStateProbe : IProbe
{
    public string Name => "mechanic.shrine-state";
    public string Group => "mechanic";
    public string Description => "Capture shrine targetability and StateMachine arrays for unused/used state diffing.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx) => MechanicStateCapture.Capture(ctx,
        "Shrine",
        snapshot => snapshot.Kind == EntityListReader.EntityKind.Shrine
                 || snapshot.Path.Contains("/Shrines/", StringComparison.OrdinalIgnoreCase));

    public ProbeResult Discover(ProbeContext ctx) => Validate(ctx);
}

public sealed class RitualStateProbe : IProbe
{
    public string Name => "mechanic.ritual-state";
    public string Group => "mechanic";
    public string Description => "Capture Ritual rune/blocker entities and StateMachine arrays for fresh/active/completed state diffing.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx) => MechanicStateCapture.Capture(ctx,
        "Ritual",
        snapshot => snapshot.Path.Contains("/Ritual/", StringComparison.OrdinalIgnoreCase)
                 || snapshot.Metadata.Contains("/Ritual/", StringComparison.OrdinalIgnoreCase));

    public ProbeResult Discover(ProbeContext ctx) => Validate(ctx);
}

public sealed class EldritchAltarUiProbe : IProbe
{
    public string Name => "mechanic.eldritch-altar-ui";
    public string Group => "mechanic";
    public string Description => "Capture Eldritch altar entities and the visible two-choice ground-label UI tree for a current-build scoring/click contract.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var entityEvidence = MechanicStateCapture.Capture(ctx,
            "Eldritch altar",
            snapshot => IsAltarPath(snapshot.Path) || IsAltarPath(snapshot.Metadata));
        var snapshot = new GameSnapshot(
            ctx.Reader, ctx.Chain.IngameData, ctx.Chain.IngameState,
            new WindowInfo(0, 0, 1920, 1080));
        var labels = snapshot.GroundLabels
            .Where(label => IsAltarPath(label.Path))
            .ToArray();
        if (labels.Length == 0)
            return new ProbeResult(
                entityEvidence.Status,
                "No Eldritch altar choice label is visible. Stand within network/render range of an unconsumed altar before capturing."
                + Environment.NewLine + entityEvidence.Message,
                entityEvidence.Candidates);

        var lines = new List<string>
        {
            $"Visible Eldritch altar labels: {labels.Length}",
        };
        foreach (var label in labels)
        {
            var rect = label.LabelRect;
            lines.Add($"entityId={label.EntityId} label=0x{(long)label.LabelElementAddress:X} " +
                      $"visible={label.IsLabelVisible} rect={(rect is { } r ? $"{r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0}" : "none")} path={label.Path}");
            lines.AddRange(DumpTree(ctx.Reader, label.LabelElementAddress));
        }
        lines.Add(entityEvidence.Message);
        return ProbeResult.Pass(string.Join(Environment.NewLine, lines));
    }

    public ProbeResult Discover(ProbeContext ctx) => Validate(ctx);

    internal static bool IsAltarPath(string path)
        => path.Contains("PrimordialBosses/TangleAltar", StringComparison.OrdinalIgnoreCase)
        || path.Contains("PrimordialBosses/CleansingFireAltar", StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<string> DumpTree(MemoryReader reader, nint root)
    {
        var output = new List<string>();
        var queue = new Queue<(nint Address, string Path, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, "label", 0));
        while (queue.Count > 0 && seen.Count < 512)
        {
            var (address, path, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 256);
            if (element is null) continue;
            var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
            if (string.IsNullOrWhiteSpace(text))
                text = NativeString.Read(reader, address + KnownOffsets.Element.Text);
            var rect = ElementGeometry.TryReadRect(reader, address);
            output.Add($"  {path} addr=0x{(long)address:X} children={element.Children.Count} " +
                       $"visible={ElementReader.IsVisibleDeep(reader, address)} " +
                       $"rect={(rect is { } r ? $"{r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0}" : "none")} " +
                       $"text='{text.Replace("\r", " ").Replace("\n", " | ")}'");
            if (depth >= 10) continue;
            for (var index = 0; index < element.Children.Count; index++)
                queue.Enqueue((element.Children[index], $"{path}/{index}", depth + 1));
        }
        return output;
    }
}

/// <summary>
/// Validates the committed production contract (<see cref="EldritchAltarChoicesReader"/>)
/// against a live altar — the same code path the bot clicks from. Distinct from
/// <c>mechanic.eldritch-altar-ui</c>, which dumps the raw tree for (re)discovery.
/// </summary>
public sealed class EldritchAltarChoicesProbe : IProbe
{
    public string Name => "mechanic.eldritch-altar-choices";
    public string Group => "mechanic";
    public string Description => "Validate EldritchAltarChoicesReader against a live altar: parsed top/bottom texts + click rects, fail-closed on shape drift.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var snapshot = new GameSnapshot(
            ctx.Reader, ctx.Chain.IngameData, ctx.Chain.IngameState,
            new WindowInfo(0, 0, 1920, 1080));
        var lines = new List<string>();
        int altars = 0, parsed = 0;
        foreach (var label in snapshot.GroundLabels)
        {
            if (!EldritchAltarUiProbe.IsAltarPath(label.Path)) continue;
            altars++;
            var choices = label.EldritchAltarChoices;
            if (choices is null)
            {
                lines.Add($"entityId={label.EntityId}: choice tree NOT readable (fail-closed) path={label.Path}");
                continue;
            }
            parsed++;
            lines.Add($"entityId={label.EntityId} path={label.Path}");
            foreach (var (side, choice) in new[] { ("TOP", choices.Top), ("BOTTOM", choices.Bottom) })
            {
                var r = choice.ClickRect;
                lines.Add($"  {side} rect={r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0} " +
                          $"center=({r.CenterX:F0},{r.CenterY:F0})");
                foreach (var line in choice.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    lines.Add($"    | {line}");
            }
        }
        if (altars == 0)
            return ProbeResult.Skip(
                "No Eldritch altar choice label in render range. Stand next to an unconsumed altar.");
        var summary = $"altar labels={altars} parsed={parsed}";
        return parsed == altars
            ? ProbeResult.Pass(summary + Environment.NewLine + string.Join(Environment.NewLine, lines))
            : ProbeResult.Fail(summary + " — shape drift, run mechanic.eldritch-altar-ui to recapture"
                + Environment.NewLine + string.Join(Environment.NewLine, lines));
    }

    public ProbeResult Discover(ProbeContext ctx) => Validate(ctx);
}

public sealed class RitualShopUiProbe : IProbe
{
    public string Name => "mechanic.ritual-shop-ui";
    public string Group => "mechanic";
    public string Description => "Capture the current-build Ritual reward window tree, tribute/reroll text, typed item offers, and purchase controls.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        if (!ctx.Reader.TryReadStruct<nint>(
                ctx.Chain.IngameState + KnownOffsets.IngameState.IngameUi, out var ingameUi)
            || ingameUi == 0
            || !ctx.Reader.TryReadStruct<nint>(
                ingameUi + KnownOffsets.IngameUiElements.RitualWindow, out var panel)
            || panel == 0)
            return ProbeResult.Fail("RitualWindow pointer did not resolve");

        if (!ElementReader.IsVisibleDeep(ctx.Reader, panel))
            return ProbeResult.Skip(
                "RitualWindow is allocated but not visible. Complete the Ritual chain and open its reward shop, then rerun this probe.");

        var lines = new List<string>
        {
            $"RitualWindow=0x{(long)panel:X}",
        };
        var globalButton = RitualRewardsButtonView.FromIngameUi(ctx.Reader, ctx.Chain.IngameState);
        lines.Add($"GlobalFavoursButton visible={globalButton.IsVisible} " +
            $"progress={globalButton.Completed}/{globalButton.Total} " +
            $"rect={(globalButton.ClickRect is { } globalRect ? $"{globalRect.X:F0},{globalRect.Y:F0},{globalRect.Width:F0},{globalRect.Height:F0}" : "none")}");
        if (ctx.Reader.TryReadStruct<nint>(
                ctx.Chain.IngameState + KnownOffsets.IngameState.UIRoot, out var uiRoot))
            lines.Add($"UIRoot=0x{(long)uiRoot:X}");
        if (ctx.Reader.TryReadStruct<nint>(
                ingameUi + KnownOffsets.IngameUiElements.LeagueMechanicButtons, out var mechanicButtons)
            && mechanicButtons != 0)
        {
            lines.Add($"LeagueMechanicButtons=0x{(long)mechanicButtons:X}");
            lines.AddRange(EldritchAltarUiProbe.DumpTree(ctx.Reader, mechanicButtons)
                .Select(line => $"mechanic-buttons {line}"));
        }
        if (ctx.Reader.TryReadStruct<nint>(
                ctx.Chain.IngameState + KnownOffsets.IngameState.UIHover, out var hover)
            && hover != 0)
        {
            lines.Add($"UIHover=0x{(long)hover:X}");
            var ancestor = hover;
            for (var depth = 0; depth < 16 && ancestor != 0; depth++)
            {
                var rect = ElementGeometry.TryReadRect(ctx.Reader, ancestor);
                var text = NativeString.Read(ctx.Reader, ancestor + KnownOffsets.Element.TextNoTags);
                lines.Add($"hover-parent[{depth}]=0x{(long)ancestor:X} " +
                    $"rect={(rect is { } r ? $"{r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0}" : "none")} " +
                    $"visible={ElementReader.IsVisibleDeep(ctx.Reader, ancestor)} text='{text}'");
                if (!ctx.Reader.TryReadStruct<nint>(ancestor + KnownOffsets.Element.Parent, out var parent)) break;
                var parentSnapshot = parent == 0 ? null : ElementReader.TryReadSnapshot(ctx.Reader, parent, 256);
                var childIndex = parentSnapshot?.Children.ToList().FindIndex(child => child == ancestor) ?? -1;
                lines.Add($"hover-parent-edge[{depth}] childIndex={childIndex}");
                ancestor = parent;
            }
        }
        lines.AddRange(EldritchAltarUiProbe.DumpTree(ctx.Reader, panel));
        lines.AddRange(DumpOffers(ctx.Reader, panel));
        return ProbeResult.Pass(string.Join(Environment.NewLine, lines));
    }

    public ProbeResult Discover(ProbeContext ctx) => Validate(ctx);

    private static IReadOnlyList<string> DumpOffers(MemoryReader reader, nint panel)
    {
        var lines = new List<string>();
        var queue = new Queue<(nint Address, string Path)>();
        var seenElements = new HashSet<nint>();
        var seenItems = new HashSet<nint>();
        var validOffers = 0;
        queue.Enqueue((panel, "label"));

        while (queue.Count > 0 && seenElements.Count < 512)
        {
            var (address, path) = queue.Dequeue();
            if (!seenElements.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, maxChildren: 64);
            if (element is null) continue;

            if (reader.TryReadStruct<nint>(address + KnownOffsets.NormalInventoryItem.Item, out var item)
                && item != 0
                && seenItems.Add(item)
                && EntityListReader.ReadEntityPath(reader, item) is { } metadata
                && metadata.StartsWith("Metadata/Items/", StringComparison.Ordinal))
            {
                validOffers++;
                var components = EntityComponents.ReadComponentMap(reader, item);
                var baseName = "";
                var rarity = -1;
                var identified = false;
                var itemLevel = 0;
                var stack = 1;
                var resource = "";
                if (components.TryGetValue("Base", out var baseAddress)
                    && reader.TryReadStruct<nint>(baseAddress + KnownOffsets.BaseComponent.ItemInfo, out var info)
                    && info != 0)
                    baseName = NativeString.Read(reader, info + KnownOffsets.ItemInfo.BaseName);
                if (components.TryGetValue("Mods", out var modsAddress))
                {
                    reader.TryReadStruct(modsAddress + KnownOffsets.ModsComponent.ItemRarity, out rarity);
                    reader.TryReadStruct(modsAddress + KnownOffsets.ModsComponent.Identified, out identified);
                    reader.TryReadStruct(modsAddress + KnownOffsets.ModsComponent.ItemLevel, out itemLevel);
                }
                if (components.TryGetValue("Stack", out var stackAddress)
                    && reader.TryReadStruct<int>(stackAddress + KnownOffsets.StackComponent.CurrentCount, out var count)
                    && count > 0)
                    stack = count;
                if (components.TryGetValue("RenderItem", out var renderAddress)
                    && reader.TryReadStruct<nint>(renderAddress + KnownOffsets.RenderItemComponent.ResourcePath, out var resourcePtr)
                    && resourcePtr != 0)
                    resource = reader.ReadStringUtf16(resourcePtr, 260);

                reader.TryReadStruct(address + KnownOffsets.NormalInventoryItem.Width, out int width);
                reader.TryReadStruct(address + KnownOffsets.NormalInventoryItem.Height, out int height);
                reader.TryReadStruct<nint>(address + KnownOffsets.Element.Tooltip, out var tooltip);
                var rect = ElementGeometry.TryReadRect(reader, address);
                lines.Add($"offer {path} element=0x{(long)address:X} item=0x{(long)item:X} " +
                    $"rect={(rect is { } r ? $"{r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0}" : "none")} " +
                    $"size={width}x{height} base='{baseName}' path='{metadata}' rarity={rarity} " +
                    $"identified={identified} ilvl={itemLevel} stack={stack} resource='{resource}' " +
                    $"tooltip=0x{(long)tooltip:X}");
                if (tooltip != 0)
                    lines.AddRange(EldritchAltarUiProbe.DumpTree(reader, tooltip)
                        .Select(line => $"tooltip[{path}] {line}"));
            }

            for (var index = 0; index < element.Children.Count; index++)
                queue.Enqueue((element.Children[index], $"{path}/{index}"));
        }

        lines.Insert(0, $"offers={validOffers}");
        return lines;
    }

}

internal static class MechanicStateCapture
{
    private const int StateCount = 24;

    public static ProbeResult Capture(
        ProbeContext ctx,
        string label,
        Func<EntityListReader.EntitySnapshot, bool> predicate)
    {
        if (ctx.Chain.EntityList == 0) return ProbeResult.Fail("EntityList pointer null");

        var traversal = EntityListReader.EnumerateEntityAddresses(ctx.Reader, ctx.Chain.EntityList);
        var matches = traversal.EntityAddresses
            .Select(address => EntityListReader.TryReadSnapshot(ctx.Reader, address))
            .Where(snapshot => snapshot is not null && predicate(snapshot))
            .Cast<EntityListReader.EntitySnapshot>()
            .OrderBy(snapshot => snapshot.Id)
            // Blight-ravaged maps can expose 300+ pathway markers before reward chests in
            // entity-ID order. A 128 cap silently omitted every chest from the supposedly
            // complete encounter capture, so retain the full practical mechanic set.
            .Take(2048)
            .ToArray();

        if (matches.Length == 0)
            return ProbeResult.Skip($"no {label} entities in the current network bubble; run this probe during each mechanic phase");

        var lines = new List<string>(matches.Length + 1)
        {
            $"{label}: {matches.Length} matching entities; traversal badReads={traversal.BadReads}",
        };
        foreach (var snapshot in matches)
        {
            snapshot.Components.TryGetValue("StateMachine", out var stateMachine);
            var stateCount = snapshot.Path.EndsWith("/BlightPump", StringComparison.OrdinalIgnoreCase)
                ? BlightStates.Pump.Count
                : snapshot.Path.Contains("RitualRuneInteractable", StringComparison.OrdinalIgnoreCase)
                ? RitualStates.RuneInteractable.Count
                : snapshot.Path.Contains("Objects/Afflictionator", StringComparison.OrdinalIgnoreCase)
                    ? SimulacrumStates.Monolith.CaptureCount
                : snapshot.Path.Contains("/Ritual/RitualRune", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : StateCount;
            var states = stateMachine == 0
                ? "none"
                : string.Join(",", StateMachineView.ReadValues(ctx.Reader, stateMachine, stateCount)
                    .Select((value, index) => $"{index}:{value}"));
            var grid = snapshot.GridPosition is { } gp ? $"({gp.X},{gp.Y})" : "?";
            var componentEvidence = "";
            if (snapshot.Components.TryGetValue("Shrine", out var shrine) && shrine != 0)
            {
                var bytes = new List<string>();
                for (var offset = 0x20; offset <= 0x28; offset++)
                    bytes.Add(ctx.Reader.TryReadStruct<byte>(shrine + offset, out var value)
                        ? $"{offset:X2}:{value:X2}"
                        : $"{offset:X2}:??");
                componentEvidence = $" shrine=0x{(long)shrine:X} shrineBytes=[{string.Join(",", bytes)}]";
            }
            if (snapshot.Components.TryGetValue("Chest", out var chest) && chest != 0)
            {
                var opened = ctx.Reader.TryReadStruct<byte>(
                    chest + KnownOffsets.ChestComponent.IsOpened, out var openedValue)
                    ? openedValue.ToString()
                    : "?";
                var locked = ctx.Reader.TryReadStruct<byte>(
                    chest + KnownOffsets.ChestComponent.IsLocked, out var lockedValue)
                    ? lockedValue.ToString()
                    : "?";
                var strongbox = ctx.Reader.TryReadStruct<byte>(
                    chest + KnownOffsets.ChestComponent.IsStrongbox, out var strongboxValue)
                    ? strongboxValue.ToString()
                    : "?";
                componentEvidence += $" chest=0x{(long)chest:X} opened={opened} locked={locked} strongbox={strongbox}";
            }
            if (snapshot.Path.Contains("Objects/Afflictionator", StringComparison.OrdinalIgnoreCase)
                && ctx.Oracle.IsAvailable)
            {
                const string expression =
                    "var e = EntityListWrapper.OnlyValidEntities.FirstOrDefault(x => " +
                    "x.Metadata != null && x.Metadata.Contains(\"Objects/Afflictionator\")); " +
                    "var s = e?.GetComponent<StateMachine>(); " +
                    "s == null ? \"\" : string.Join(\",\", s.States.Select((v,i) => i + \":\" + v.Name + \"=\" + v.Value))";
                componentEvidence += ctx.Oracle.TryEval(expression, out var namedStates)
                    ? $" oracleOrderedStates=[{namedStates}]"
                    : " oracleOrderedStates=[unavailable]";
            }
            if (snapshot.Path.EndsWith("/BlightPump", StringComparison.OrdinalIgnoreCase)
                && ctx.Oracle.IsAvailable)
            {
                const string expression =
                    "var e = EntityListWrapper.OnlyValidEntities.FirstOrDefault(x => " +
                    "x.Metadata != null && x.Metadata.EndsWith(\"/BlightPump\")); " +
                    "var s = e?.GetComponent<StateMachine>(); " +
                    "s == null ? \"\" : string.Join(\",\", s.States.Select((v,i) => i + \":\" + v.Name + \"=\" + v.Value))";
                componentEvidence += ctx.Oracle.TryEval(expression, out var namedStates)
                    ? $" oracleOrderedStates=[{namedStates}]"
                    : " oracleOrderedStates=[unavailable]";
            }
            lines.Add($"id={snapshot.Id} address=0x{(long)snapshot.Address:X} kind={snapshot.Kind} " +
                      $"grid={grid} targetable={snapshot.IsTargetable?.ToString() ?? "unknown"} " +
                      $"canBeTarget={snapshot.StateMachine?.CanBeTarget.ToString() ?? "unknown"} " +
                      $"inTarget={snapshot.StateMachine?.InTarget.ToString() ?? "unknown"} " +
                      $"path={snapshot.Path} metadata={snapshot.Metadata} " +
                      $"components=[{string.Join(",", snapshot.Components.Keys.Order())}] " +
                      $"stateMachine=0x{(long)stateMachine:X} states=[{states}]{componentEvidence}");
        }

        return ProbeResult.Pass(string.Join(Environment.NewLine, lines));
    }
}
