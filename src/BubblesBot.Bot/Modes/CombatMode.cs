using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Combat;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// Drive-by combat. The walk skill stays held continuously; the cursor drifts toward
/// whichever enemy is the current target. When that enemy is in attack range AND the
/// attack is ready, tap the attack key — cursor's already on the enemy because we're
/// walking at it. After the tap, the next tick keeps walking, cursor stays on the
/// (still-alive) enemy until it dies, then moves to the next.
///
/// <para><b>Why not "halt and cast."</b> PoE's actual gameplay is fluid — you keep
/// movement going while attacks fire. Releasing the walk hold to attack costs ~100 ms of
/// dead time per tap, makes the bot look stuttery, and fails outright for held-walk skills
/// like Whirling Blades that ARE the attack. The drive-by pattern reuses the same cursor
/// position for both movement aim and attack aim — simpler and faster.</para>
///
/// <para>Tree shape:
/// <code>
/// Selector "combat"
///   ├── If(low HP) → halt + idle (let flasks recover)
///   ├── If(no enemies) → halt + idle
///   └── Sequence "engage"
///        ├── WalkTo(closest enemy)              ← keeps walk hold + cursor on enemy
///        └── If(in attack range) → Cast attack  ← cursor already on target, just tap
/// </code>
/// </para>
/// </summary>
public sealed class CombatMode : IBotMode
{
    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly Func<LivePlayer?>   _getLive;
    private readonly Func<EntityCache?>  _getEntities;
    private readonly SettingsStore _settings;
    private readonly MovementSystem _movement;
    private readonly CombatSystem  _combat = new();
    private readonly FlaskSystem   _flasks = new();
    private readonly SkillBook     _skills = new();
    private readonly IBehavior     _root;

    public string Name => "Combat";
    public IBehavior Root => _root;
    public string LastDecision { get; private set; } = "init";

    public CombatMode(SettingsStore settings, Func<GameSnapshot?> getSnapshot, Func<LivePlayer?> getLive, Func<EntityCache?> getEntities)
    {
        _settings    = settings;
        _getSnapshot = getSnapshot;
        _getLive     = getLive;
        _getEntities = getEntities;
        _movement    = new MovementSystem(settings.Current);

        _root = new Selector("combat",
            // HP guard — bail and let flasks/regen catch up. Walk hold released here is fine
            // since we're not actively engaging.
            new If("low HP", LowHp,
                new Sequence("retreat",
                    new StopMoving("halt", _movement),
                    new Behaviors.Action("wait HP", _ => BehaviorStatus.Success))),

            // No enemies → idle. Same release-walk reasoning as low-HP branch.
            new If("no enemies", NoEnemiesInRange,
                new Sequence("idle",
                    new StopMoving("halt", _movement),
                    new Behaviors.Action("wait", _ => BehaviorStatus.Success))),

            // Engage: parallel — walk and attack both run every tick. Walk keeps the cursor
            // pointed at the enemy and the move key held; Cast taps the attack key when
            // ready. They share the cursor position, no halts needed.
            new Behaviors.Parallel("engage", Behaviors.Parallel.Policy.RequireOne,
                new WalkTo("approach", _movement, ctx => ClosestEnemyGrid(ctx), arrivalRadius: 6f),
                new If("in attack range", InAttackRange,
                    new Cast("attack", _combat, ctx => PickAttackSlot(ctx.Settings),
                        Aim.AtClosestEnemy(60f), _skills))));
    }

    public void Reset()
    {
        _movement.Release();
        _combat.StopAllChannels();
        _flasks.Reset();
        _skills.Reset();
        _root.Reset();
        LastDecision = "reset";
    }

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        if (snapshot.Player is { } pv) _skills.SetActorContext(pv.ActorComponentAddress);
        if (_skills.CooldownReader is null) _skills.CooldownReader = new SkillCooldownReader(snapshot.Reader);

        var ctx = new BehaviorContext(snapshot, input, _settings.Current, _getLive(), _getEntities());

        // Flasks tick in parallel with everything else.
        _flasks.Tick(ctx);

        var status = _root.Tick(ctx);
        var slot = PickAttackSlot(ctx.Settings);
        var enemyGrid = ClosestEnemyGrid(ctx);
        LastDecision = slot is null
            ? $"no Attack skill bound (status={status})"
            : enemyGrid is null
                ? $"{slot.Name} idle (no enemies)"
                : $"{slot.Name} engaging ({enemyGrid.Value.X},{enemyGrid.Value.Y}) status={status}";
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

    private static bool NoEnemiesInRange(BehaviorContext ctx) => ClosestEnemyGrid(ctx) is null;

    /// <summary>Closest hostile alive monster within engage range, by Euclidean grid distance.</summary>
    private static Vector2i? ClosestEnemyGrid(BehaviorContext ctx)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var p = ctx.Live.Value.GridPosition;
        var r = ctx.Settings.CombatEngageRange;
        var r2 = r * r;
        EntityCache.Entry? best = null;
        var bestD2 = float.PositiveInfinity;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (!e.IsHostileMonster || !e.IsAlive || !e.IsTargetable) continue;
            var dx = (float)(e.GridPosition.X - p.X);
            var dy = (float)(e.GridPosition.Y - p.Y);
            var d2 = dx * dx + dy * dy;
            if (d2 > r2) continue;
            if (d2 < bestD2) { bestD2 = d2; best = e; }
        }
        return best?.GridPosition;
    }

    /// <summary>True when the closest enemy is within the configured attack range (skill MaxRangeGrid).</summary>
    private static bool InAttackRange(BehaviorContext ctx)
    {
        var slot = PickAttackSlot(ctx.Settings);
        if (slot is null || ctx.Live is null) return false;
        var enemy = ClosestEnemyGrid(ctx);
        if (enemy is null) return false;
        var p = ctx.Live.Value.GridPosition;
        var dx = (float)(enemy.Value.X - p.X);
        var dy = (float)(enemy.Value.Y - p.Y);
        return dx * dx + dy * dy <= slot.MaxRangeGrid * slot.MaxRangeGrid;
    }
}
