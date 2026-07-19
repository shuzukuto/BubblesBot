using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Strategies;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

public enum MapRunPhase
{
    Preparation,
    Deposit,
    Device,
    Entry,
    Clear,
    BossMechanics,
    Completion,
    Exit,
    Report,
    Stopped,
}

/// <summary>
/// Stacked-deck farming orchestrator: <b>load a staged map → enter → clear → leave via Town
/// Portal → stash loot → repeat</b>. Chains the existing pieces — <see cref="MapDeviceSystem"/>
/// (load), <see cref="PushCombatMode"/> in orchestrated mode (clear),
/// <see cref="LeaveMapSystem"/> (F-key exit), <see cref="StashDepositSystem"/> (deposit).
///
/// <para><b>Spans area transitions.</b> Unlike the other modes, this one is intentionally NOT
/// reset by <c>BotApp</c> on area change — it has to remember its place across hideout↔map
/// boundaries and keep cross-map counters. It watches the area hash itself and, on each
/// transition, resets its sub-systems (which ARE per-area) while preserving the high-level
/// step and the maps-completed / items-stashed tallies.</para>
///
/// <para><b>Stop conditions</b> (each disarms the bot via <see cref="BotSettings.BotActive"/>):
/// target map count reached, storage empty (no maps left), out of mapping resources (the F-key
/// produces no Town Portal → almost certainly out of Portal Scrolls), or player death. After a
/// stop, re-arming starts a fresh run.</para>
/// </summary>
public sealed class MapRunMode : IBotMode
{
    private enum Step { Boot, Hideout, EnterMapWait, Map, LeaveWait, Report }

    private readonly SettingsStore        _settings;
    private readonly StrategyStore        _strategies;
    private readonly Func<GameSnapshot?>  _getSnapshot;
    private readonly Func<LivePlayer?>    _getLive;
    private readonly Func<EntityCache?>   _getEntities;
    private readonly Diagnostics.IRunReporter _reporter;
    private readonly Func<LootLedger.SnapshotData> _getLoot;

    // Per-map run-report accounting (wall-clock; reports are wall-clock, not monotonic).
    private DateTime _mapStartedUtc = DateTime.UtcNow;
    private float _lootChaosAtMapStart;
    private int _pickupsAtMapStart;
    private int _mapIndex;

    /// <summary>
    /// The strategy pinned for the current run. Refreshed from <see cref="StrategyStore.Active"/>
    /// only while in hideout (the transaction boundary), so swapping the active strategy mid-map
    /// takes effect on the NEXT map rather than mutating a run in flight.
    /// </summary>
    private FarmingStrategy? _activeStrategy;

    private readonly MovementSystem    _movement;
    private readonly SkillBook         _skills;
    private readonly MapDeviceSystem   _mapDevice;
    private readonly LeaveMapSystem    _leaveMap;
    private readonly StashDepositSystem _stashDeposit;
    private readonly PushCombatMode    _mapFarming;
    private readonly AreaTransitionTracker _entryTransition = new(TimeSpan.FromSeconds(8));

    private Step     _step = Step.Boot;
    private uint     _lastAreaHash;
    private bool     _needDeposit;
    private TimeSpan _deviceFailedAt;
    private int _deviceFailures;

    // Cross-run telemetry / counters (survive area changes).
    private int      _mapsCompleted;
    private int      _itemsStashed;
    private int      _portalScrollsRemaining;
    private bool     _stopped;
    private string   _stopReason = "";
    private string   _phase = "init";
    private MapRunPhase _lifecyclePhase = MapRunPhase.Preparation;
    private string   _runId = Guid.NewGuid().ToString("N");

    private const int VK_ESCAPE          = 0x1B;
    private const int DeviceRetryBackoffSec = 5;
    private const int MaxDeviceFailures = 3;

    public string Name => "Map farming";
    public IBehavior Root => _mapFarming.Root;   // dashboard shows the clear tree (most active sub-flow)
    private string _lastDecision = "init";
    public string LastDecision 
    {
        get => _lastDecision;
        private set 
        {
            if (_lastDecision != value)
            {
                _lastDecision = value;
                BubblesBot.Bot.Diagnostics.EventLog.Emit("decision", "decision.update", BubblesBot.Bot.Diagnostics.EventSeverity.Info, value);
            }
        }
    }

    // ── Telemetry surface (read by BotApp.BuildStatus → "loop" block) ──
    // Telemetry falls back to the store's active strategy so the dashboard shows the selected
    // strategy even before the first armed tick pins it for a run.
    private FarmingStrategy? TelemetryStrategy => _activeStrategy ?? _strategies.Active;

    public string LoopPhase => _phase;
    public MapRunPhase LifecyclePhase => _lifecyclePhase;
    public string Preset => TelemetryStrategy?.Identity.Name ?? "(no strategy)";
    public string ResourcePolicy => (TelemetryStrategy?.Loot.DepositAfterEachMap ?? false)
        ? "deposit-after-map" : "continuous-until-inventory-stop";
    public string CurrentStep => _step.ToString();
    public int  MapsCompleted => _mapsCompleted;
    public int  TargetMaps => TelemetryStrategy?.Completion.TargetMaps ?? 0;
    public int  ItemsStashed => _itemsStashed + (_stashDeposit.IsBusy ? _stashDeposit.Deposited : 0);
    public int  PortalScrollsRemaining => _portalScrollsRemaining;
    public bool IsStopped => _stopped;
    public string StopReason => _stopReason;
    public string RunId => _runId;
    public AreaTransitionState EntryTransition => _entryTransition.State;
    public AreaTransitionState ExitTransition => _leaveMap.Transition;
    public object? MapTelemetry => _mapFarming.Telemetry;
    public IReadOnlyList<string> HudLines => _mapFarming.HudLines;

    public MapRunMode(SettingsStore settings, CombatCoordinator coord, StrategyStore strategies,
        Func<GameSnapshot?> getSnapshot,
        Func<LivePlayer?> getLive, Func<EntityCache?> getEntities,
        Diagnostics.IRunReporter reporter, Func<LootLedger.SnapshotData> getLoot)
    {
        _settings    = settings;
        _strategies  = strategies;
        _getSnapshot = getSnapshot;
        _getLive     = getLive;
        _getEntities = getEntities;
        _reporter    = reporter;
        _getLoot     = getLoot;
        // One shared movement/skills authority with the combat brain.
        _movement    = coord.Movement;
        _skills      = coord.Skills;
        _mapDevice   = new MapDeviceSystem(_movement, _skills, getSnapshot, getLive, getEntities);
        _leaveMap    = new LeaveMapSystem(_movement, () => settings.Current.StackedDeckPortalKeyVk, IsHideout);
        _stashDeposit = new StashDepositSystem(
            _movement, _skills, getSnapshot,
            (inventory, item) => MapInventoryPolicy.ShouldRetainForNextRun(
                inventory.Items, item));
        _mapFarming  = new PushCombatMode(settings, coord, getSnapshot, getLive, getEntities,
            orchestrated: true, getStrategy: () => _activeStrategy);
    }

    public void Reset() => ResetRun();

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        if (snapshot.Player is { } pv) _skills.SetActorContext(pv.ActorComponentAddress);
        if (_skills.CooldownReader is null) _skills.CooldownReader = new SkillCooldownReader(snapshot.Reader);

        // Pin the active strategy while in a safe hub (the run transaction boundary); keep the
        // pinned one for the rest of the run so a mid-map swap only takes effect next map. A run
        // with no valid strategy fails loud — never silently maps with defaults.
        var pinned = new BehaviorContext(snapshot, input, _settings.Current, _getLive(), _getEntities());
        if (_activeStrategy is null || IsHideout(pinned)) _activeStrategy = _strategies.Active;

        var ctx = new BehaviorContext(snapshot, input, _settings.Current, _getLive(), _getEntities(), _activeStrategy);

        // A stopped run reaches this method only after the user explicitly re-arms it.
        if (_stopped) ResetRun();

        if (_activeStrategy is null && !_stopped)
        {
            Stop("no active farming strategy — select one in the web UI (Strategies tab) before arming map farming");
            return;
        }

        // Internal area-change handling — we are NOT auto-reset, so detect transitions here.
        var hash = snapshot.AreaHash;
        if (hash != 0 && hash != _lastAreaHash)
        {
            if (_lastAreaHash != 0) OnAreaChanged(ctx);
            _lastAreaHash = hash;
        }

        if (_stopped) return;

        // Death → stop + disarm.
        if (ctx.Live is { } lv && lv.HpMax > 0 && lv.HpCurrent <= 0)
        {
            Stop("player died");
            return;
        }

        // Refresh portal-scroll telemetry whenever the inventory is readable (hideout / stash open).
        var inv = snapshot.Inventory;
        if (inv.IsOpen) _portalScrollsRemaining = inv.PortalScrollCount();

        switch (_step)
        {
            case Step.Boot:         TickBoot(ctx); break;
            case Step.Hideout:      TickHideout(ctx); break;
            case Step.EnterMapWait: TickEnterMapWait(ctx); break;
            case Step.Map:          TickMap(snapshot, input, ctx); break;
            case Step.LeaveWait:    TickLeave(ctx); break;
            case Step.Report:       TickReport(); break;
        }

        LastDecision = $"step={_step} {_phase} maps={_mapsCompleted}/{TargetMaps}";
    }

    // ─── Steps ───────────────────────────────────────────────────────────

    private void TickBoot(BehaviorContext ctx)
    {
        _lifecyclePhase = MapRunPhase.Preparation;
        if (IsHideout(ctx))
        {
            _needDeposit = ShouldDeposit;
            _step = Step.Hideout;
            _phase = "in hideout";
        }
        else if (ctx.Snapshot.Player is not null)
        {
            // Armed inside a map already — pick up the clear loop.
            _step = Step.Map;
            _phase = "in map";
            MarkMapStart();
        }
        else
        {
            _phase = "waiting to classify area";
        }
    }

    private void TickHideout(BehaviorContext ctx)
    {
        var snapshot = ctx.Snapshot;

        // A failed/retried map-device attempt can leave the Atlas covering the world.
        // Stash interactions behind that panel cannot resolve, so close it before the
        // deposit preflight owns movement/clicks.
        if (_needDeposit && snapshot.AtlasPanel.IsVisible && !snapshot.IsStashOpen)
        {
            ctx.Input.VerifiedTapKey(
                VK_ESCAPE, ClickIntent.InteractUi, "close atlas before map deposit",
                expectResolved: () => !(_getSnapshot()?.AtlasPanel.IsVisible ?? true),
                timeoutMs: 2000);
            _phase = "closing atlas before deposit";
            return;
        }

        // 1. Deposit loot (opens the stash, which also opens the inventory so items read).
        if (_needDeposit && ShouldDeposit)
        {
            _lifecyclePhase = MapRunPhase.Deposit;
            if (!_stashDeposit.IsBusy
                && _stashDeposit.CurrentPhase is not (StashDepositSystem.Phase.Done or StashDepositSystem.Phase.Failed))
                _stashDeposit.Start(_activeStrategy?.Supply.DumpTabName ?? "");

            var r = _stashDeposit.Tick(ctx);
            _phase = $"deposit: {_stashDeposit.Status}";
            if (r == StashDepositSystem.Result.InProgress) return;

            _itemsStashed += _stashDeposit.Deposited;
            if (r == StashDepositSystem.Result.Failed)
            {
                Stop($"stash deposit incomplete: {_stashDeposit.Status}");
                return;
            }
            _needDeposit = false;
        }

        // 2. Close any open panel (stash/inventory) before clicking the map device in-world.
        if (snapshot.IsStashOpen)
        {
            ctx.Input.TapKey(VK_ESCAPE, ClickIntent.InteractUi, "close stash");
            _phase = "closing panels";
            return;
        }

        // 3. Stop if we've hit the target map count.
        if (_mapsCompleted >= (_activeStrategy?.Completion.TargetMaps ?? int.MaxValue))
        {
            Stop($"target map count reached ({_mapsCompleted})");
            return;
        }

        // 4. Load the next map via the device flow (with a small backoff after a failure).
        _lifecyclePhase = MapRunPhase.Device;
        if (_mapDevice.CurrentPhase == MapDeviceSystem.Phase.Failed)
        {
            if (_deviceFailures >= MaxDeviceFailures)
            {
                Stop($"map device retry budget exhausted ({_deviceFailures}/{MaxDeviceFailures}): {_mapDevice.Status}");
                return;
            }
            if ((BotMonotonicClock.Now - _deviceFailedAt).TotalSeconds < DeviceRetryBackoffSec)
            {
                _phase = $"map device failed — retrying soon ({_mapDevice.Status})";
                return;
            }
            _mapDevice.Start(ctx.Entities);
        }
        else if (!_mapDevice.IsBusy)
        {
            _mapDevice.Start(ctx.Entities);
        }

        // Storage-empty detection is owned by MapDeviceSystem.TickSelectMap ("no maps in
        // storage", handled below). A mode-level "atlas visible && storage empty" check is
        // WRONG: ctrl+clicking the LAST stored map into the device empties storage while a
        // map sits staged, and the check killed the loop right there (live 2026-07-15).

        var dr = _mapDevice.Tick(ctx);
        if (dr == MapDeviceSystem.Result.Failed)
        {
            _deviceFailedAt = BotMonotonicClock.Now;
            _deviceFailures++;
            if (_mapDevice.Status.Contains("no maps", StringComparison.OrdinalIgnoreCase))
            {
                Stop("out of maps (storage empty)");
                return;
            }
        }

        // Once the device flow is walking into the portal, the area change is imminent.
        if (_mapDevice.CurrentPhase == MapDeviceSystem.Phase.EnterPortal)
        {
            _entryTransition.Start(snapshot.AreaHash, AreaRole.SafeHub, AreaRole.Map,
                AreaTransitionTracker.MonotonicNow());
            _step = Step.EnterMapWait;
        }

        _phase = $"load: {_mapDevice.Status}";
    }

    private void TickEnterMapWait(BehaviorContext ctx)
    {
        _lifecyclePhase = MapRunPhase.Entry;
        var transition = _entryTransition.Observe(
            ctx.Snapshot.AreaHash, WorldAreaClassifier.Classify(ctx), AreaTransitionTracker.MonotonicNow());
        if (transition.Outcome == AreaTransitionOutcome.Confirmed)
        {
            ResetSubsystems();
            _deviceFailures = 0;
            _step = Step.Map;
            _phase = "entered map - destination verified";
            MarkMapStart();
            return;
        }
        if (transition.Outcome is AreaTransitionOutcome.UnexpectedDestination or AreaTransitionOutcome.TimedOut)
        {
            Stop($"map entry {transition.Outcome}: expected {transition.ExpectedDestination}, " +
                 $"observed {transition.ObservedRole} at 0x{transition.ObservedAreaHash:X8}");
            return;
        }
        if (transition.Outcome == AreaTransitionOutcome.VerifyingDestination)
        {
            _phase = "entered area - verifying map destination";
            return;
        }

        // Keep driving the device flow (it's clicking/walking into the portal). The area-change
        // tracker flips us to Map once the destination has positive map evidence.
        var dr = _mapDevice.Tick(ctx);
        if (dr == MapDeviceSystem.Result.Failed)
        {
            _deviceFailedAt = BotMonotonicClock.Now;
            _deviceFailures++;
            _step = Step.Hideout;   // fall back; hideout step retries with backoff
            _phase = $"enter-portal failed: {_mapDevice.Status}";
            return;
        }
        _phase = $"entering map: {_mapDevice.Status}";
    }

    private void TickMap(GameSnapshot snapshot, IInputRouter input, BehaviorContext ctx)
    {
        // A checkpoint resurrection or manual emergency exit can return us to hideout while
        // the high-level step still says Map. Never let the clear controller explore/cast in
        // a safe hub and never credit the abandoned map. Re-enter normal hideout preflight;
        // the next device activation will replace any surviving portal set.
        if (IsHideout(ctx))
        {
            ResetSubsystems();
            _needDeposit = ShouldDeposit;
            _step = Step.Hideout;
            _lifecyclePhase = MapRunPhase.Preparation;
            _phase = "returned to hideout before map completion - restarting preflight";
            BubblesBot.Bot.Diagnostics.EventLog.Emit(
                "maprun", "maprun.uncredited-hideout-return",
                BubblesBot.Bot.Diagnostics.EventSeverity.Warning,
                "safe hub observed during map clear; abandoned map was not credited");
            return;
        }

        _lifecyclePhase = HasUniqueTarget(ctx)
            ? MapRunPhase.BossMechanics
            : MapRunPhase.Clear;
        // Drive the clear tree (exitOnClear=false → it halts on clear instead of taking a
        // transition; combat + loot + flasks all run inside the production map-clear controller).
        _mapFarming.Tick(snapshot, input);
        _phase = $"clearing: {_mapFarming.LastDecision}";

        if (_mapFarming.IsCleared)
        {
            _lifecyclePhase = MapRunPhase.Completion;
            _leaveMap.Start(ctx);
            _step = Step.LeaveWait;
            _phase = "cleared — leaving";
        }
    }

    private void TickLeave(BehaviorContext ctx)
    {
        _lifecyclePhase = MapRunPhase.Exit;
        var r = _leaveMap.Tick(ctx);
        _phase = $"leaving: {_leaveMap.Status}";
        if (r == LeaveMapSystem.Result.Failed)
            Stop($"could not leave map: {_leaveMap.Status}");
        else if (r == LeaveMapSystem.Result.Succeeded)
        {
            CreditMap();
            ResetSubsystems();
            _step = Step.Report;
            _phase = "map credited - reporting";
        }
    }

    private void TickReport()
    {
        _lifecyclePhase = MapRunPhase.Report;
        _needDeposit = ShouldDeposit;
        _step = Step.Hideout;
        _phase = $"reported map {_mapsCompleted}/{TargetMaps}; preset={Preset}";
    }

    // ─── Transitions / lifecycle ───────────────────────────────────────────

    private void OnAreaChanged(BehaviorContext ctx)
    {
        switch (_step)
        {
            case Step.EnterMapWait:
                _phase = "area changed - verifying map destination";
                break;
            case Step.LeaveWait:
                // LeaveMapSystem owns destination verification and map credit. Do not reset it
                // merely because some area transition occurred.
                _phase = "area changed - verifying hideout";
                break;
            case Step.Map:
                // Multi-zone maps, side areas, and boss arenas legitimately change the area hash.
                // The nested map-clear controller owns those transitions and does not set
                // IsCleared until its reachable zone graph is exhausted. Never credit here.
                _phase = "map subzone changed - continuing clear";
                break;
            default:
                ResetSubsystems();
                _step = Step.Boot;
                _phase = "area changed — reclassifying";
                break;
        }
    }

    private void CreditMap()
    {
        _mapsCompleted++;
        BubblesBot.Bot.Diagnostics.EventLog.Log("MapRun",
            $"map completed — {_mapsCompleted}/{TargetMaps}");
        EmitReport("completed", "");
    }

    /// <summary>Snapshot loot + start the clock for a new map's run report.</summary>
    private void MarkMapStart()
    {
        _mapStartedUtc = DateTime.UtcNow;
        _mapIndex++;
        try
        {
            var loot = _getLoot();
            _lootChaosAtMapStart = loot.TotalChaos;
            _pickupsAtMapStart = loot.Pickups;
        }
        catch { /* loot ledger unavailable — deltas start from 0 */ }
    }

    /// <summary>Emit a run report for the just-finished map. Never throws into the tick loop.</summary>
    private void EmitReport(string result, string stopReason)
    {
        try
        {
            var loot = _getLoot();
            var strategy = TelemetryStrategy;
            var snap = _getSnapshot();
            _reporter.Report(new Diagnostics.RunReport(
                RunId: $"{_runId}-{_mapIndex}",
                SessionId: _runId,
                Mode: 4,
                ModeName: Name,
                StrategyId: strategy?.Identity.Id ?? "",
                StrategyName: strategy?.Identity.Name ?? "(none)",
                Profile: snap?.Player?.CharacterName ?? "",
                League: snap?.League ?? "",
                MapName: strategy?.Supply.Map.TargetMapName ?? "",
                StartedUtc: _mapStartedUtc,
                EndedUtc: DateTime.UtcNow,
                DurationSec: Math.Max(0, (DateTime.UtcNow - _mapStartedUtc).TotalSeconds),
                Result: result,
                StopReason: stopReason,
                MapIndex: _mapIndex,
                MapsCompleted: _mapsCompleted,
                LootChaos: Math.Max(0, loot.TotalChaos - _lootChaosAtMapStart),
                LootChaosCumulative: loot.TotalChaos,
                ChaosPerHour: loot.ChaosPerHour,
                ItemsPicked: Math.Max(0, loot.Pickups - _pickupsAtMapStart),
                Deaths: result == "died" ? 1 : 0));
        }
        catch (Exception ex)
        {
            BubblesBot.Bot.Diagnostics.EventLog.Emit("incident", "run-report.emit-failed",
                Diagnostics.EventSeverity.Warning, $"run report emit failed: {ex.Message}");
        }
    }

    private void ResetSubsystems()
    {
        _movement.Release();
        _mapDevice.Cancel();
        _leaveMap.Cancel();
        _stashDeposit.Cancel();
        _mapFarming.Reset();
    }

    private void ResetRun()
    {
        ResetSubsystems();
        _step = Step.Boot;
        _runId = Guid.NewGuid().ToString("N");
        _needDeposit = false;
        _deviceFailedAt = TimeSpan.Zero;
        _deviceFailures = 0;
        _mapsCompleted = 0;
        _itemsStashed = 0;
        _portalScrollsRemaining = 0;
        _stopped = false;
        _stopReason = "";
        _phase = "init";
        _lifecyclePhase = MapRunPhase.Preparation;
        LastDecision = "reset";
        _entryTransition.Reset();
    }

    private void Stop(string reason)
    {
        // A map in progress ended abnormally (death, stuck, device failure) — capture it. Clean
        // stops in hideout (target reached, no strategy) already reported per map via CreditMap.
        if (_step is Step.Map or Step.LeaveWait)
            EmitReport(reason.Contains("died", StringComparison.OrdinalIgnoreCase) ? "died" : "stopped", reason);

        _stopped = true;
        _stopReason = reason;
        _phase = $"STOPPED: {reason}";
        _lifecyclePhase = MapRunPhase.Stopped;
        ResetSubsystems();
        _settings.Mutate(s => s.BotActive = false);   // disarm
        BubblesBot.Bot.Diagnostics.EventLog.Log("MapRun", $"STOPPED + disarmed: {reason}");
        LastDecision = $"STOPPED: {reason}";
    }

    // ─── World classification ──────────────────────────────────────────────

    private static bool IsHideout(BehaviorContext ctx)
        => WorldAreaClassifier.Classify(ctx) == AreaRole.SafeHub;

    private bool ShouldDeposit => _activeStrategy?.Loot.DepositAfterEachMap ?? false;

    private static bool HasUniqueTarget(BehaviorContext ctx)
    {
        if (ctx.Entities is null) return false;
        foreach (var entity in ctx.Entities.Entries.Values)
            if (TargetEligibility.IsEligible(entity) && Threat.RarityRank(entity) >= 3)
                return true;
        return false;
    }
}
