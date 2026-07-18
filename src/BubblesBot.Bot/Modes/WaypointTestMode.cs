using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;
using BubblesBot.Core.Snapshot;
using BubblesBot.Core; // LandmarkCatalog

namespace BubblesBot.Bot.Modes;

/// <summary>
/// First end-to-end navigation test. Goal: pathfind to the area's waypoint and click it.
/// Tree shape:
/// <code>
/// Selector "waypoint test"
///   ├── If(in click range &amp; label visible) → Click waypoint
///   └── If(waypoint exists) → FollowPath toward it
/// </code>
///
/// <para><b>Range threshold.</b> PoE lets you click a waypoint when its label is visible and
/// the player is within ~25 grid cells. We arrive via FollowPath at <c>GoalArrivalRadius</c>
/// (12 cells) and let the click branch take over. Both branches re-evaluate every tick — if
/// the player gets pushed out of range, FollowPath resumes.</para>
///
/// <para><b>Held-key cleanup.</b> Switching from FollowPath (running) to the click branch
/// requires releasing the move key first; otherwise the held key would keep nudging the
/// character past the click. The Click branch does <see cref="StopMoving"/> first.</para>
/// </summary>
public sealed class WaypointTestMode : IBotMode
{
    private const string WaypointPath  = "Metadata/MiscellaneousObjects/Waypoint";

    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly Func<LivePlayer?>   _getLive;
    private readonly SettingsStore _settings;
    private readonly MovementSystem _movement;
    private readonly InteractSystem _interact = new();
    private readonly SkillBook _skills = new();
    private readonly FollowPath _follow;
    private readonly IBehavior _root;
    private DateTime _lastClickAt = DateTime.MinValue;
    // Latched while the map UI is OPEN. Drops the moment the user closes the panel so a
    // second test pass (rearm → walk → re-click) works without an area change.
    private bool _panelOpenLatch;
    // Last seen waypoint grid for the current area. The waypoint entity only loads inside
    // the network bubble (~200 grid), so the label vanishes when the player walks far away.
    // Caching the position once we've seen it lets the bot path back from anywhere in the area.
    private Vector2i? _rememberedWaypoint;
    private uint _rememberedForArea;

    public string Name => "Waypoint test";
    public IBehavior Root => _root;
    public string LastDecision { get; private set; } = "init";
    public FollowPath FollowPathBehavior => _follow;

    public WaypointTestMode(
        SettingsStore settings,
        Func<GameSnapshot?> getSnapshot,
        Func<LivePlayer?> getLive)
    {
        _settings    = settings;
        _getSnapshot = getSnapshot;
        _getLive     = getLive;
        _movement    = new MovementSystem(settings.Current);
        _follow      = new FollowPath("follow→waypoint", _movement, FindWaypointGrid, _skills);

        _root = new Selector("waypoint test",
            // Branch 0: while the world map panel is open our work is done — halt and idle.
            // Panel-close drops the latch automatically (in Tick) so a second test pass works.
            new If("panel open",
                _ => _panelOpenLatch,
                new Sequence("done",
                    new StopMoving("halt", _movement),
                    new Behaviors.Action("idle", _ => BehaviorStatus.Success))),

            // Branch 1: in range → stop moving and click.
            new If("in click range",
                ctx => InClickRange(ctx) && WaypointLabel(ctx) is { LabelRect: not null, IsLabelVisible: true },
                new Sequence("click waypoint",
                    new StopMoving("halt", _movement),
                    new Behaviors.Action("click", ClickWaypoint))),

            // Branch 2: walk toward it.
            _follow);
    }

    public void Reset()
    {
        _movement.Release();
        _interact.Cancel();
        _skills.Reset();
        _panelOpenLatch = false;
        _rememberedWaypoint = null;
        _rememberedForArea = 0;
        _root.Reset();
        LastDecision = "reset";
    }

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        // Drop remembered waypoint when the area changes (different instance, different layout).
        if (_rememberedForArea != 0 && snapshot.AreaHash != _rememberedForArea)
        {
            _rememberedWaypoint = null;
            _rememberedForArea = 0;
        }

        // Track the world-map panel's open/close state. When the panel closes (manually or
        // by area change) the latch drops so the next test pass starts cleanly.
        _panelOpenLatch = snapshot.IsLeftPanelOpen;

        var ctx = new BehaviorContext(snapshot, input, _settings.Current, _getLive());

        // Cache live waypoint position whenever we can see one in this area — used as a
        // fallback when the player walks out of network range and the label vanishes.
        if (FindWaypointGridLive(ctx) is { } liveGrid)
        {
            _rememberedWaypoint = liveGrid;
            _rememberedForArea = snapshot.AreaHash;
        }

        var status = _root.Tick(ctx);

        var grid = FindWaypointGrid(ctx);
        var player = _getLive()?.GridPosition;
        var dist = (grid is { } g && player is { } p)
            ? MathF.Sqrt((g.X - p.X) * (float)(g.X - p.X) + (g.Y - p.Y) * (float)(g.Y - p.Y))
            : float.NaN;

        var src = grid is null ? "" : (FindWaypointGridLive(ctx) is null ? " [cached]" : "");
        LastDecision = grid is null
            ? $"no waypoint known (status={status})"
            : $"waypoint @ ({grid.Value.X},{grid.Value.Y}){src} dist={dist:F1} {_follow.LastDecision}";
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Live label-channel read. Null when player is out of network bubble.</summary>
    private static Vector2i? FindWaypointGridLive(BehaviorContext ctx)
    {
        foreach (var l in ctx.Snapshot.GroundLabels)
        {
            if (l.Path != WaypointPath) continue;
            if (l.EntityGridPosition is { } g) return g;
        }
        return null;
    }

    /// <summary>
    /// Three-tier lookup, in order:
    /// <list type="number">
    ///   <item>Live ground-label scan (in-bubble, freshest position).</item>
    ///   <item>Cached last-seen position for this area (out-of-bubble fallback).</item>
    ///   <item><see cref="TileMapView"/> landmark lookup — works even at zone load before
    ///         we've ever seen the entity, because tile data is baked into the area at load.</item>
    /// </list>
    /// </summary>
    private Vector2i? FindWaypointGrid(BehaviorContext ctx)
    {
        var live = FindWaypointGridLive(ctx);
        if (live is not null) return live;
        if (_rememberedForArea == ctx.Snapshot.AreaHash && _rememberedWaypoint is { } cached) return cached;

        var player = ctx.Live?.GridPosition ?? default;
        var fromTiles = ctx.Snapshot.TileMap.FindNearestLandmark(LandmarkCatalog.Kind.Waypoint, player);
        return fromTiles;
    }

    private static GroundLabelView? WaypointLabel(BehaviorContext ctx)
    {
        foreach (var l in ctx.Snapshot.GroundLabels)
            if (l.Path == WaypointPath) return l;
        return null;
    }

    private bool InClickRange(BehaviorContext ctx)
    {
        var live = ctx.Live;
        var g = FindWaypointGrid(ctx);
        if (live is null || g is null) return false;
        var p = live.Value.GridPosition;
        var dx = (float)(g.Value.X - p.X);
        var dy = (float)(g.Value.Y - p.Y);
        var r = ctx.Settings.InteractionRangeGrid;
        if (dx * dx + dy * dy > r * r) return false;
        // LOS gate — must use the *walkable* layer, not targeting. PoE marks walls that you
        // can shoot/blink over with pf=0/tgt>0; targeting LOS would let us "click through"
        // them. Walkable LOS = "is there a clean walking line to the target."
        var pf = ctx.Snapshot.Nav.PathReader;
        return pf is null || PathSmoother.HasLineOfSight(pf, p.X, p.Y, g.Value.X, g.Value.Y, minValue: 1);
    }

    private BehaviorStatus ClickWaypoint(BehaviorContext ctx)
    {
        // Throttle clicks just enough to span the input gate's settle window. The
        // panel-open latch handles "stop after success" — this throttle only prevents
        // back-to-back fires when the first click hasn't even left the queue yet.
        if ((DateTime.UtcNow - _lastClickAt).TotalSeconds < 0.25) return BehaviorStatus.Running;

        var label = WaypointLabel(ctx);
        if (label?.LabelRect is not { } rect) return BehaviorStatus.Failure;
        var (sx, sy) = ctx.Snapshot.Window.ToScreen(rect.CenterX, rect.CenterY);
        var ticket = ctx.Input.Click(sx, sy, ClickIntent.InteractWorld, "click waypoint",
            expectResolved: () =>
            {
                // Resolved when PoE opens the World Map UI (left panel slot becomes non-null).
                // Falling back to label-disappearance doesn't work for waypoints — the label
                // stays visible while the destination chooser is open, so the gate would
                // hang the full timeout and we'd queue another click.
                var snap = _getSnapshot();
                return snap is null || snap.IsLeftPanelOpen;
            },
            timeoutMs: 1500);
        if (!ticket.Accepted) return BehaviorStatus.Failure;
        _lastClickAt = DateTime.UtcNow;
        return BehaviorStatus.Success;
    }
}
