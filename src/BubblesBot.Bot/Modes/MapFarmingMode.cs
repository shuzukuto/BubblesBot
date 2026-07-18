using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Combat;
using BubblesBot.Bot.Behaviors.Interact;
using BubblesBot.Bot.Behaviors.Loot;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// First end-to-end map-running mode. Composes the existing combat / loot / exploration /
/// transition behaviors into a single tree:
///
/// <code>
/// Selector "map farm"
///   ├── If(low HP) → halt + idle (let flasks recover)
///   ├── If(enemies in engage range) → halt + Cast attack
///   ├── If(loot in engage range) → halt + LootClosestVisible
///   ├── If(map cleared / explored) → EnterAreaTransition (back to hideout)
///   └── ExploreFrontier (default — walk to the next unvisited landmark)
/// </code>
///
/// <para>Flasks tick in parallel via <see cref="FlaskSystem"/>. Hideout setup (place a
/// map in the device, click open) is NOT wired in this skeleton — that requires the map
/// device entity click sequence which we haven't validated yet. For v1, the user enters a
/// map manually, sets Mode = MapFarming, and the bot clears + exits. The stash deposit
/// loop is the next milestone after MapDeviceMode lands.</para>
///
/// <para><b>"Map cleared" heuristic</b> is intentionally rough — there's no PoE flag for
/// "you killed everything." We approximate by: no enemies in bubble for N seconds AND
/// no unvisited landmarks remaining. Tunable per testing.</para>
/// </summary>
public sealed class MapFarmingMode : IBotMode
{
    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly Func<LivePlayer?>   _getLive;
    private readonly Func<EntityCache?>  _getEntities;
    private readonly SettingsStore _settings;

    private readonly MovementSystem  _movement;
    private readonly CombatSystem    _combat = new();
    private readonly FlaskSystem     _flasks = new();
    private readonly InteractSystem  _interact = new();
    private readonly SkillBook       _skills = new();
    private readonly ExplorationSystem _exploration = new();
    private readonly LootClosestVisible _loot;
    private readonly IBehavior _root;

    private DateTime _lastEnemyAt = DateTime.UtcNow;

    public string Name => "Map farming";
    public IBehavior Root => _root;
    public string LastDecision { get; private set; } = "init";

    public MapFarmingMode(SettingsStore settings, Func<GameSnapshot?> getSnapshot, Func<LivePlayer?> getLive, Func<EntityCache?> getEntities)
    {
        _settings    = settings;
        _getSnapshot = getSnapshot;
        _getLive     = getLive;
        _getEntities = getEntities;
        _movement    = new MovementSystem(settings.Current);
        _loot        = new LootClosestVisible("loot closest", _interact, getSnapshot);

        // Mechanic interaction. Each variant uses InteractWorldEntity with a kind-specific
        // target selector + activation predicate. The selectors return null when no
        // takable mechanic of that kind exists, which causes Failure and lets the parent
        // Selector fall through to the next branch.
        var shrineInteract = new Behaviors.Interact.InteractWorldEntity("shrine", _interact, _movement, _skills,
            ctx => ctx.Settings.TakeShrines ? PickClosest(ctx, MechanicKind.Shrine) : null,
            (_, e) => e.Path.StartsWith("Metadata/Shrines/", StringComparison.Ordinal) && false);
        // ↑ Activation predicate stub: shrine has no exposed StateMachine flag we read yet.
        // The behavior's verify-loop relies on the entity disappearing from the cache /
        // becoming non-targetable post-click. For v1 the click attempt counter caps retries
        // so we don't stall forever if this misfires.

        var ritualInteract = new Behaviors.Interact.InteractWorldEntity("ritual", _interact, _movement, _skills,
            ctx => ctx.Settings.TakeRituals ? PickClosest(ctx, MechanicKind.RitualRune) : null,
            (_, _) => false);  // ritual activation is deferred — combat mode picks up after the click

        var altarInteract = new Behaviors.Interact.InteractWorldEntity("altar", _interact, _movement, _skills,
            ctx => PickAltarByPolicy(ctx),
            (_, _) => false);

        // Drive-by combat shape:
        //   • Walk hold stays continuous via the bottom branches (ExploreFrontier or
        //     EnterAreaTransition). They keep the move key held + cursor on the walk target.
        //   • Combat runs in PARALLEL — when an enemy is in attack range, Cast taps the
        //     attack key. The aim resolves to the closest enemy, briefly redirecting the
        //     cursor for the tap. Next tick movement re-asserts cursor on the walk target.
        //   • Loot interrupts (halt + click) only when an item is close enough that it's
        //     worth stopping for; transient loot drops while running pass us by until we
        //     loop back. Tunable via LootRangeGrid.
        //   • Halt-on-low-HP is the only "stop everything" branch.
        _root = new Selector("map farm",
            new If("low HP", LowHp,
                new Sequence("retreat",
                    new StopMoving("halt", _movement),
                    new Behaviors.Action("wait HP", _ => BehaviorStatus.Success))),
            // Mechanics — take shrines / rituals / altars before exploration. Each variant
            // skips itself via its target-selector when the user disabled it or no entity
            // of that kind is available. Order: shrines first (fastest, near-zero risk),
            // rituals next (kicks off combat), altars last (per-policy, default skip).
            new If("shrine available",
                ctx => ctx.Settings.TakeShrines && PickClosest(ctx, MechanicKind.Shrine) is not null,
                shrineInteract),
            new If("ritual available",
                ctx => ctx.Settings.TakeRituals && PickClosest(ctx, MechanicKind.RitualRune) is not null,
                ritualInteract),
            new If("altar available",
                ctx => ctx.Settings.AltarPolicy != 0 && PickAltarByPolicy(ctx) is not null,
                altarInteract),
            // Loot is the one case we deliberately halt for — loot click needs a stable
            // cursor on the label rect to land. Quick stop-loot-resume.
            new If("loot in range", HasLootInRange,
                new Sequence("loot",
                    new StopMoving("halt loot", _movement),
                    _loot)),
            // Map-cleared check before exploration — taking the portal home should preempt
            // running back to the entrance for re-exploration.
            new If("map cleared", MapCleared,
                new Behaviors.Parallel("clear+exit", Behaviors.Parallel.Policy.RequireAll,
                    new EnterAreaTransition("exit", _interact, _movement, _skills, getSnapshot),
                    new If("attack passing mob", InAttackRange,
                        new Cast("attack", _combat, ctx => PickAttackSlot(ctx.Settings),
                            Aim.AtClosestEnemy(60f), _skills)))),
            // Default branch: explore + drive-by attack in parallel. Walk continues; cursor
            // briefly visits enemies for taps; flasks tick from FlaskSystem.Tick.
            new Behaviors.Parallel("explore+attack", Behaviors.Parallel.Policy.RequireOne,
                new ExploreFrontier("explore", _exploration, _movement, _skills),
                new If("attack in range", InAttackRange,
                    new Cast("attack", _combat, ctx => PickAttackSlot(ctx.Settings),
                        Aim.AtClosestEnemy(60f), _skills))));
    }

    public void Reset()
    {
        _movement.Release();
        _combat.StopAllChannels();
        _flasks.Reset();
        _interact.Cancel();
        _skills.Reset();
        _exploration.Reset();
        _loot.Reset();
        _root.Reset();
        _lastEnemyAt = DateTime.UtcNow;
        LastDecision = "reset";
    }

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        if (snapshot.Player is { } pv) _skills.SetActorContext(pv.ActorComponentAddress);
        if (_skills.CooldownReader is null) _skills.CooldownReader = new SkillCooldownReader(snapshot.Reader);

        var ctx = new BehaviorContext(snapshot, input, _settings.Current, _getLive(), _getEntities());

        // Track last-seen-enemy for the "map cleared" heuristic.
        if (HasEnemiesInRange(ctx)) _lastEnemyAt = DateTime.UtcNow;

        _flasks.Tick(ctx);
        var status = _root.Tick(ctx);
        LastDecision = $"status={status} sinceEnemy={(DateTime.UtcNow - _lastEnemyAt).TotalSeconds:F0}s";
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static SkillSlot? PickAttackSlot(BotSettings settings)
    {
        foreach (var s in settings.Skills.Slots)
            if (s.Role == SkillRole.Attack && s.Vk != 0) return s;
        return null;
    }

    private static bool LowHp(BehaviorContext ctx)
    {
        var live = ctx.Live;
        if (live is null || live.Value.HpMax <= 0) return false;
        var th = ctx.Settings.HpRetreatThreshold;
        if (th <= 0) return false;
        return (float)live.Value.HpCurrent / live.Value.HpMax < th;
    }

    private static bool HasEnemiesInRange(BehaviorContext ctx)
    {
        if (ctx.Entities is null || ctx.Live is null) return false;
        var p = ctx.Live.Value.GridPosition;
        var r = ctx.Settings.CombatEngageRange;
        var r2 = r * r;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (!e.IsHostileMonster || !e.IsAlive || !e.IsTargetable) continue;
            var dx = (float)(e.GridPosition.X - p.X);
            var dy = (float)(e.GridPosition.Y - p.Y);
            if (dx * dx + dy * dy <= r2) return true;
        }
        return false;
    }

    /// <summary>
    /// True when a hostile is within the configured attack-skill range. Used as the gate
    /// on the parallel attack branch — fires while exploration walks past mobs.
    /// </summary>
    private static bool InAttackRange(BehaviorContext ctx)
    {
        var slot = PickAttackSlot(ctx.Settings);
        if (slot is null || ctx.Live is null || ctx.Entities is null) return false;
        var p = ctx.Live.Value.GridPosition;
        var r2 = (float)slot.MaxRangeGrid * slot.MaxRangeGrid;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (!e.IsHostileMonster || !e.IsAlive || !e.IsTargetable) continue;
            var dx = (float)(e.GridPosition.X - p.X);
            var dy = (float)(e.GridPosition.Y - p.Y);
            if (dx * dx + dy * dy <= r2) return true;
        }
        return false;
    }

    /// <summary>
    /// Pick the closest unactivated mechanic of <paramref name="kind"/> within a generous
    /// search radius — anything in the network bubble qualifies. The bot will path to it
    /// before resuming exploration.
    /// </summary>
    private static EntityCache.Entry? PickClosest(BehaviorContext ctx, MechanicKind kind)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var view = new MechanicsView(ctx.Entities, ctx.Snapshot.Reader);
        var hit = view.Closest(ctx.Live.Value.GridPosition, maxRangeGrid: 200f, kind: kind);
        // Periodic visibility into what the picker considered. Throttled to 1Hz per kind so
        // it doesn't flood the log buffer.
        var key = $"PickClosest:{kind}";
        if ((DateTime.UtcNow - GetLastLog(key)).TotalSeconds > 1.0)
        {
            var avail = view.Available(kind).ToList();
            var p = ctx.Live.Value.GridPosition;
            var summary = string.Join(" ", avail.Take(5).Select(m =>
                $"id={m.Id}@({m.GridPosition.X},{m.GridPosition.Y})d={DistGrid(p, m.GridPosition):F0}"));
            BubblesBot.Bot.Diagnostics.EventLog.Log("Mechanics",
                $"{kind}: {avail.Count} avail [{summary}] picked={(hit is null ? "none" : hit.Id.ToString())}");
            SetLastLog(key);
        }
        return hit?.Entry;
    }

    private static readonly Dictionary<string, DateTime> _lastLogTimes = new();
    private static DateTime GetLastLog(string key) => _lastLogTimes.TryGetValue(key, out var t) ? t : DateTime.MinValue;
    private static void SetLastLog(string key) => _lastLogTimes[key] = DateTime.UtcNow;
    private static float DistGrid(Vector2i a, Vector2i b) { var dx = (float)(a.X-b.X); var dy = (float)(a.Y-b.Y); return MathF.Sqrt(dx*dx+dy*dy); }

    /// <summary>
    /// Eldritch altar pick by user policy. Skip = always null. Always-top/bottom = pick
    /// among grouped TangleAltar/CleansingFireAltar pairs. Pairs are matched by physical
    /// proximity — same altar instance has both choice entities within ~25 grid of each
    /// other. Smart policy currently falls back to Skip until Element.Text + the mod
    /// scorer land.
    /// </summary>
    private static EntityCache.Entry? PickAltarByPolicy(BehaviorContext ctx)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var policy = ctx.Settings.AltarPolicy;
        if (policy == 0 || policy == 3) return null;  // skip + smart-fallback

        var view = new MechanicsView(ctx.Entities, ctx.Snapshot.Reader);
        var altars = view.Available(MechanicKind.EldritchAltar)
            .OrderBy(e => DistanceTo(ctx.Live.Value.GridPosition, e.GridPosition))
            .ToList();
        if (altars.Count == 0) return null;

        // Group altars by proximity: paired choice buttons sit within ~25 grid units of
        // each other. We pick the closest pair first, then use the policy to choose top
        // (lower Y in PoE's grid — top-of-screen) vs bottom (higher Y).
        var nearest = altars[0];
        var partner = altars.Skip(1).FirstOrDefault(a =>
            Math.Abs(a.GridPosition.X - nearest.GridPosition.X) +
            Math.Abs(a.GridPosition.Y - nearest.GridPosition.Y) <= 25);
        if (partner is null) return nearest.Entry;  // singleton — only one choice visible

        // PoE renders the "top" option visually higher on screen, which corresponds to a
        // SMALLER Y in grid space (grid Y increases southward in projection). Note: flip
        // this if testing shows the opposite — easy fix.
        var top    = nearest.GridPosition.Y < partner.GridPosition.Y ? nearest : partner;
        var bottom = nearest.GridPosition.Y < partner.GridPosition.Y ? partner : nearest;
        return policy == 1 ? top.Entry : bottom.Entry;
    }

    private static float DistanceTo(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static bool HasLootInRange(BehaviorContext ctx)
    {
        var r = ctx.Settings.LootRangeGrid;
        foreach (var l in ctx.Snapshot.GroundLabels)
        {
            if (!l.IsItem || !l.IsLabelVisible) continue;
            if (l.DistanceToPlayer <= r) return true;
        }
        return false;
    }

    private bool MapCleared(BehaviorContext ctx)
    {
        // Heuristic: nothing's been hostile for 12s AND no unvisited landmarks remain.
        // Tune with testing — current values are conservative starting points.
        if ((DateTime.UtcNow - _lastEnemyAt).TotalSeconds < 12) return false;
        return _exploration.PickFrontier(ctx) is null;
    }
}
