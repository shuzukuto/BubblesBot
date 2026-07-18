using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Combat;
using BubblesBot.Bot.Behaviors.Loot;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// First composed-from-systems mode. Skeleton only — no map-device, no waypoint, no exit
/// flow yet. The point is the composition pattern: a behavior tree that picks the highest-
/// priority sub-task each tick.
///
/// <para>Tree shape:</para>
/// <code>
/// Selector "mapping"
///   ├── If(low life) → flask + halt          (TODO: flask system)
///   ├── If(enemies in range) → Cast at AOE cluster
///   ├── If(loot in range) → loot tree
///   └── walk toward closest enemy or stand idle
/// </code>
///
/// <para>Each branch is a behavior; the same Cast/WalkTo/Loot building blocks compose into
/// any future mode. No code is duplicated between this and a future BossMode or DelveMode —
/// the only thing those add is a different selector ordering and additional leaf behaviors.</para>
/// </summary>
public sealed class MappingMode : IBotMode
{
    private const float EnemyEngageRange = 50f;
    private const float LootEngageRange = 50f;

    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly Func<LivePlayer?>   _getLive;
    private readonly Func<EntityCache?>  _getEntities;
    private readonly SettingsStore _settings;

    private readonly InteractSystem  _interact = new();
    private readonly MovementSystem  _movement;
    private readonly CombatSystem    _combat;
    private readonly LootClosestVisible _loot;
    private readonly IBehavior _root;

    public string Name => "Mapping";
    public IBehavior Root => _root;
    public string LastLootDecision => _loot.LastDecision;

    public MappingMode(
        SettingsStore settings,
        Func<GameSnapshot?> getSnapshot,
        Func<LivePlayer?> getLive,
        Func<EntityCache?> getEntities)
    {
        _settings = settings;
        _getSnapshot = getSnapshot;
        _getLive = getLive;
        _getEntities = getEntities;
        _movement = new MovementSystem(settings.Current);
        _combat   = new CombatSystem();
        _loot     = new LootClosestVisible("loot closest", _interact, getSnapshot);

        _root = new Selector("mapping",
            // Combat — clear what's in range first.
            new If("enemies in range",
                ctx => HasEnemyWithin(ctx, EnemyEngageRange),
                new Sequence("engage cluster",
                    new StopMoving("halt", _movement),
                    new Cast("aoe cast", _combat, ctx => PickAttackSlot(ctx.Settings), Aim.AtAoeCluster(EnemyEngageRange)))),

            // Loot — when nothing to fight, pick up valuables.
            new If("loot in range",
                ctx => HasLootWithin(ctx, LootEngageRange),
                new Sequence("loot pass",
                    new StopMoving("halt for loot", _movement),
                    _loot)),

            // Otherwise — walk toward the next interesting thing.
            new WalkTo("approach", _movement, NextDestination));
    }

    public void Reset()
    {
        _movement.Release();
        _combat.StopAllChannels();
        _interact.Cancel();
        _loot.Reset();
        _root.Reset();
    }

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        var ctx = new BehaviorContext(snapshot, input, _settings.Current, _getLive(), _getEntities());
        _root.Tick(ctx);
    }

    // ── Selectors ─────────────────────────────────────────────────────────

    private static SkillSlot? PickAttackSlot(BotSettings settings)
    {
        foreach (var s in settings.Skills.Slots)
            if (s.Role == SkillRole.Attack && s.Vk != 0) return s;
        return null;
    }

    private static bool HasEnemyWithin(BehaviorContext ctx, float gridRange)
    {
        if (ctx.Entities is null || ctx.Live is null) return false;
        var p = ctx.Live.Value.GridPosition;
        var r2 = gridRange * gridRange;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (!e.IsHostileMonster || !e.IsAlive || !e.IsTargetable) continue;
            var dx = (float)(e.GridPosition.X - p.X);
            var dy = (float)(e.GridPosition.Y - p.Y);
            if (dx * dx + dy * dy <= r2) return true;
        }
        return false;
    }

    private static bool HasLootWithin(BehaviorContext ctx, float gridRange)
    {
        foreach (var l in ctx.Snapshot.GroundLabels)
        {
            if (!l.IsItem || !l.IsLabelVisible) continue;
            if (l.DistanceToPlayer <= gridRange) return true;
        }
        return false;
    }

    private static Vector2i? NextDestination(BehaviorContext ctx)
    {
        // Cheap exploration heuristic: walk to the closest hostile mob, else closest visible
        // item, else nothing. Real frontier-based exploration is a TODO for the Mapping mode
        // proper — this skeleton just exercises the WalkTo behavior end to end.
        if (ctx.Entities is not null && ctx.Live is not null)
        {
            var p = ctx.Live.Value.GridPosition;
            EntityCache.Entry? best = null;
            var bestD2 = float.PositiveInfinity;
            foreach (var e in ctx.Entities.Entries.Values)
            {
                if (!e.IsHostileMonster || !e.IsAlive || !e.IsTargetable) continue;
                var dx = (float)(e.GridPosition.X - p.X);
                var dy = (float)(e.GridPosition.Y - p.Y);
                var d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; best = e; }
            }
            if (best is not null) return best.GridPosition;
        }

        GroundLabelView? bestLabel = null;
        var bestDist = float.PositiveInfinity;
        foreach (var l in ctx.Snapshot.GroundLabels)
        {
            if (!l.IsItem || !l.IsLabelVisible) continue;
            if (l.EntityGridPosition is not { } _) continue;
            if (l.DistanceToPlayer < bestDist) { bestDist = l.DistanceToPlayer; bestLabel = l; }
        }
        return bestLabel?.EntityGridPosition;
    }
}
