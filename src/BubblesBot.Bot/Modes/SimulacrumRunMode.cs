using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Interact;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// Cross-area Simulacrum farming lifecycle: withdraw one map item from the currently
/// visible Delirium stash tab, right-click one into the map device, enter fresh portals,
/// run the arena controller, exit, and repeat until limits or supplies stop the session.
/// </summary>
public sealed class SimulacrumRunMode : IBotMode
{
    private enum Step { Boot, Supply, Device, Arena, Recover, Stopped }
    private enum RecoveryLeg { ExitArena, AwaitSafeHub, ReturnToArena }

    private const int VkEscape = 0x1B;
    private const int VkLeftControl = 0xA2;
    private const int MaxSupplyClicks = 4;
    private static readonly TimeSpan ArenaDepartureConfirmation = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan UnknownAreaHydrationWindow = TimeSpan.FromMilliseconds(1500);

    private readonly SettingsStore _settings;
    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly Func<LivePlayer?> _getLive;
    private readonly Func<EntityCache?> _getEntities;
    private readonly MovementSystem _movement;
    private readonly SkillBook _skills;
    private readonly InteractSystem _interact = new();
    private readonly InteractWorldEntity _openStash;
    private readonly EnterAreaTransition _returnThroughPortal;
    private readonly StashTabSwitcher _supplyTabSwitcher;
    private readonly MapDeviceSystem _device;
    private readonly SimulacrumMode _arena;
    private readonly AreaTransitionTracker _entryTransition = new(TimeSpan.FromSeconds(12));
    private readonly AreaTransitionTracker _exitTransition = new(TimeSpan.FromSeconds(12));

    private Step _step = Step.Boot;
    private uint _arenaAreaHash;
    private int _runsStarted;
    private int _runsCompleted;
    private int _lastKnownInventoryCells;
    private bool _lastKnownInventoryCellsKnown;
    private int _carriedSupplies;
    private bool _supplyCommittedToDevice;
    private int _supplyClickAttempts;
    private TimeSpan _lastSupplyActionAt = TimeSpan.MinValue;
    private TimeSpan _supplyMissingSince = TimeSpan.MinValue;
    private TimeSpan _supplyPanelObservedAt = TimeSpan.MinValue;
    private TimeSpan _arenaMismatchSince = TimeSpan.MinValue;
    private TimeSpan _bootUnknownSince = TimeSpan.MinValue;
    private TimeSpan _recoveryStartedAt = TimeSpan.MinValue;
    private RecoveryLeg _recoveryLeg = RecoveryLeg.ReturnToArena;
    // Return-leg guard: true once the re-entry portal was clicked, so we wait for the arena area
    // to actually load before latching it (a premature EnterAreaTransition success once latched
    // the hideout hash and hung the run — 2026-07-16 wave-11 incident).
    private bool _reentryPortalEntered;
    private TimeSpan _reentryAwaitStartedAt = TimeSpan.MinValue;
    private string _recoveryReason = string.Empty;
    private uint _recoveryOriginAreaHash;
    private int _runDeaths;
    private bool _deathRecoveryPending;
    private bool _discardExistingRun;
    private string _supplyTarget = "none";
    private bool _stopped;
    private bool _deviceStorageChecked;
    private string _stopReason = string.Empty;
    private string _runId = Guid.NewGuid().ToString("N");

    public string Name => "Simulacrum farming";
    public IBehavior Root => _arena.Root;
    private string _lastDecision = "init";
    public string LastDecision 
    {
        get => _lastDecision;
        private set 
        {
            if (_lastDecision != value)
            {
                _lastDecision = value;
                Diagnostics.EventLog.Emit("decision", "decision.update", Diagnostics.EventSeverity.Info, value);
            }
        }
    }
    public IReadOnlyList<string> HudLines => new[] { LastDecision };
    public string RunId => _runId;
    public object Telemetry => new
    {
        lifecycleStep = _step.ToString(),
        runsStarted = _runsStarted,
        runsCompleted = _runsCompleted,
        targetRuns = _settings.Current.SimulacrumTargetRuns,
        carriedSupplies = _carriedSupplies,
        lastKnownInventoryCells = _lastKnownInventoryCells,
        devicePhase = _device.CurrentPhase.ToString(),
        deviceStatus = _device.Status,
        entryTransition = _entryTransition.State,
        exitTransition = _exitTransition.State,
        stopped = _stopped,
        stopReason = _stopReason,
        runDeaths = _runDeaths,
        deathRecoveryPending = _deathRecoveryPending,
        discardExistingRun = _discardExistingRun || _settings.Current.SimulacrumDiscardExistingRun,
        recoveryLeg = _step == Step.Recover ? _recoveryLeg.ToString() : "none",
        recoveryReason = _recoveryReason,
        recoveryOriginAreaHash = _recoveryOriginAreaHash,
        supplyTarget = _supplyTarget,
        phase = _arena.Phase.ToString(),
        arena = _arena.Telemetry,
    };

    public SimulacrumRunMode(
        SettingsStore settings,
        CombatCoordinator coord,
        Func<GameSnapshot?> getSnapshot,
        Func<LivePlayer?> getLive,
        Func<EntityCache?> getEntities)
    {
        _settings = settings;
        _getSnapshot = getSnapshot;
        _getLive = getLive;
        _getEntities = getEntities;
        // Share the one combat authority (movement/skills) with the arena combat brain.
        _movement = coord.Movement;
        _skills = coord.Skills;
        _arena = new SimulacrumMode(settings, coord, getSnapshot, getLive, getEntities);
        _device = new MapDeviceSystem(_movement, _skills, getSnapshot, getLive, getEntities);
        _supplyTabSwitcher = new StashTabSwitcher(getSnapshot);
        _openStash = new InteractWorldEntity(
            "open supply stash", _interact, _movement, _skills,
            FindStash,
            (ctx, _) => ctx.Snapshot.IsStashOpen);
        _returnThroughPortal = new EnterAreaTransition(
            "return to Simulacrum after death", _interact, _movement, _skills,
            getSnapshot,
            entity => entity.Kind == EntityListReader.EntityKind.TownPortal
                   || entity.Kind == EntityListReader.EntityKind.Portal,
            _ => _recoveryLeg == RecoveryLeg.ExitArena ? _arena.PortalAnchor : null);
    }

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        if (snapshot.Player is { } player)
            _skills.SetActorContext(player.ActorComponentAddress);
        if (_skills.CooldownReader is null)
            _skills.CooldownReader = new SkillCooldownReader(snapshot.Reader);

        if (_stopped)
        {
            Reset();
            return;
        }

        var ctx = new BehaviorContext(
            snapshot, input, _settings.Current, _getLive(), _getEntities());
        if (snapshot.Inventory.IsOpen)
        {
            _lastKnownInventoryCells = snapshot.Inventory.OccupiedCells;
            _lastKnownInventoryCellsKnown = true;
            _carriedSupplies = CountCarriedSupplies(snapshot.Inventory);
        }

        switch (_step)
        {
            case Step.Boot: TickBoot(ctx); break;
            case Step.Supply: TickSupply(ctx); break;
            case Step.Device: TickDevice(ctx); break;
            case Step.Arena: TickArena(snapshot, input, ctx); break;
            case Step.Recover: TickRecovery(ctx); break;
            case Step.Stopped: break;
        }
    }

    /// <summary>
    /// Called by the global death gate on the confirmed checkpoint-revive edge. Returning
    /// true means this run owns a validated resume-through-existing-portal recovery and the
    /// caller should keep automation armed; false preserves the global fail-safe disarm.
    /// </summary>
    public bool NotifyRevived()
    {
        if (_step != Step.Arena || _arena.Phase == SimulacrumPhase.Terminal)
            return false;
        _runDeaths++;
        _deathRecoveryPending = true;
        _arena.SeedDeathCount(_runDeaths);
        LastDecision = $"Arena/Death: checkpoint revive {_runDeaths}; awaiting existing portal";
        Diagnostics.EventLog.Emit(
            "simulacrum", "simulacrum.death-recovery-pending",
            Diagnostics.EventSeverity.Warning, LastDecision,
            new Dictionary<string, object?>
            {
                ["runDeaths"] = _runDeaths,
                ["maxDeaths"] = _settings.Current.SimulacrumMaxDeaths,
            });
        return _runDeaths <= _settings.Current.SimulacrumMaxDeaths;
    }

    public static bool ShouldResumeExistingPortal(
        bool discardExistingRun, bool existingPortalPresent)
        => !discardExistingRun && existingPortalPresent;

    public static bool ShouldAttachFreshUnknownMap(AreaTransitionState transition)
        => transition.ExpectedDestination == AreaRole.Map
        && transition.ObservedAreaHash != 0
        && transition.ObservedAreaHash != transition.OriginAreaHash
        && transition.ObservedRole == AreaRole.Unknown
        && transition.Outcome == AreaTransitionOutcome.VerifyingDestination;

    public void Reset()
    {
        _movement.Release();
        _interact.Cancel();
        _openStash.Reset();
        _supplyTabSwitcher.Reset();
        _returnThroughPortal.Reset();
        _device.Cancel();
        _arena.Reset();
        _entryTransition.Reset();
        _exitTransition.Reset();
        _step = Step.Boot;
        _arenaAreaHash = 0;
        _runsStarted = 0;
        _runsCompleted = 0;
        _deviceStorageChecked = false;
        _lastKnownInventoryCells = 0;
        _lastKnownInventoryCellsKnown = false;
        _carriedSupplies = 0;
        _deviceStorageChecked = false;
        _supplyCommittedToDevice = false;
        _supplyClickAttempts = 0;
        _lastSupplyActionAt = TimeSpan.MinValue;
        _supplyMissingSince = TimeSpan.MinValue;
        _supplyPanelObservedAt = TimeSpan.MinValue;
        _arenaMismatchSince = TimeSpan.MinValue;
        _bootUnknownSince = TimeSpan.MinValue;
        _recoveryStartedAt = TimeSpan.MinValue;
        _recoveryLeg = RecoveryLeg.ReturnToArena;
        _reentryPortalEntered = false;
        _recoveryReason = string.Empty;
        _recoveryOriginAreaHash = 0;
        _runDeaths = 0;
        _deathRecoveryPending = false;
        _discardExistingRun = false;
        _supplyTarget = "none";
        _reentryAwaitStartedAt = TimeSpan.MinValue;
        _stopped = false;
        _stopReason = string.Empty;
        _runId = Guid.NewGuid().ToString("N");
        LastDecision = "reset";
    }

    private void TickBoot(BehaviorContext ctx)
    {
        var monolith = FindMonolith(ctx);
        var role = WorldAreaClassifier.Classify(ctx);
        if (ctx.Settings.SimulacrumDiscardExistingRun
            && (monolith is not null || role == AreaRole.Map))
        {
            _discardExistingRun = true;
            _step = Step.Recover;
            _recoveryStartedAt = AreaTransitionTracker.MonotonicNow();
            _recoveryLeg = RecoveryLeg.ExitArena;
            _recoveryReason = "one-shot discard of existing Simulacrum";
            _recoveryOriginAreaHash = ctx.Snapshot.AreaHash;
            _returnThroughPortal.Reset();
            LastDecision = "Boot/Discard: exiting existing arena before fresh supply run";
            Diagnostics.EventLog.Emit(
                "simulacrum", "simulacrum.discard-started",
                Diagnostics.EventSeverity.Warning, LastDecision,
                new Dictionary<string, object?>
                {
                    ["areaHash"] = $"0x{ctx.Snapshot.AreaHash:X8}",
                });
            return;
        }
        if (monolith is not null || role == AreaRole.Map)
        {
            StartArena(ctx.Snapshot.AreaHash);
            LastDecision = monolith is not null
                ? "Boot/Arena: attached from visible Simulacrum monolith"
                : "Boot/Arena: attached to combat area; locating Simulacrum monolith";
            return;
        }
        if (role == AreaRole.Unknown && ctx.Snapshot.AreaHash != 0)
        {
            // A Simulacrum entry/re-entry can place the player in an empty network bubble:
            // no monolith, monster, portal, or stash is hydrated yet. Active mode 6 is the
            // user's explicit arena intent, so after giving entity classification time to
            // settle, attach and let SimulacrumMode perform its physical locate-start sweep.
            var now = AreaTransitionTracker.MonotonicNow();
            if (_bootUnknownSince == TimeSpan.MinValue)
                _bootUnknownSince = now;
            if (now - _bootUnknownSince >= UnknownAreaHydrationWindow)
            {
                if (ctx.Settings.SimulacrumDiscardExistingRun)
                {
                    _discardExistingRun = true;
                    _step = Step.Recover;
                    _recoveryStartedAt = now;
                    _recoveryLeg = RecoveryLeg.ExitArena;
                    _recoveryReason = "one-shot discard of stable unknown Simulacrum entrance";
                    _recoveryOriginAreaHash = ctx.Snapshot.AreaHash;
                    _returnThroughPortal.Reset();
                    LastDecision = "Boot/Discard: exiting stable unknown arena before fresh supply run";
                    Diagnostics.EventLog.Emit(
                        "simulacrum", "simulacrum.discard-started",
                        Diagnostics.EventSeverity.Warning, LastDecision,
                        new Dictionary<string, object?>
                        {
                            ["areaHash"] = $"0x{ctx.Snapshot.AreaHash:X8}",
                            ["evidence"] = "stable unknown area in explicit Simulacrum mode",
                        });
                    return;
                }
                StartArena(ctx.Snapshot.AreaHash);
                LastDecision = "Boot/Arena: attached to stable unknown area; locating Simulacrum monolith";
                return;
            }
            LastDecision = "Boot: allowing destination entities to hydrate";
            return;
        }
        _bootUnknownSince = TimeSpan.MinValue;
        if (role == AreaRole.SafeHub || ctx.Snapshot.IsStashOpen)
        {
            var existingPortal = FindExistingPortal(ctx) is not null;
            if (ShouldResumeExistingPortal(
                    ctx.Settings.SimulacrumDiscardExistingRun, existingPortal))
            {
                _step = Step.Recover;
                _recoveryLeg = RecoveryLeg.ReturnToArena;
                _recoveryStartedAt = AreaTransitionTracker.MonotonicNow();
                _recoveryOriginAreaHash = ctx.Snapshot.AreaHash;
                _recoveryReason = "boot resume through existing portal";
                _returnThroughPortal.Reset();
                LastDecision = "Boot/Recovery: existing portal takes precedence over consuming supply";
                Diagnostics.EventLog.Emit(
                    "simulacrum", "simulacrum.boot-portal-resume",
                    Diagnostics.EventSeverity.Info, LastDecision,
                    new Dictionary<string, object?>
                    {
                        ["areaHash"] = $"0x{ctx.Snapshot.AreaHash:X8}",
                    });
                return;
            }
            if (ctx.Settings.SimulacrumDiscardExistingRun)
            {
                _settings.Mutate(settings => settings.SimulacrumDiscardExistingRun = false);
                Diagnostics.EventLog.Emit(
                    "simulacrum", "simulacrum.discard-hideout-confirmed",
                    Diagnostics.EventSeverity.Info,
                    "fresh-start request began in hideout; ignoring existing portal set");
            }
            _step = Step.Device;
            _deviceStorageChecked = false;
            _device.Start(ctx.Entities, MapDeviceSystem.PayloadSource.InventorySimulacrum);
            LastDecision = ctx.Settings.SimulacrumDiscardExistingRun
                ? "Boot/Device: discard request bypassed existing portals"
                : "Boot/Device: hideout ready, checking map device storage";
            return;
        }
        LastDecision = "Boot: waiting for hideout or Simulacrum evidence";
    }

    private void TickSupply(BehaviorContext ctx)
    {
        var carried = _carriedSupplies;
        if (carried > 0)
        {
            _supplyMissingSince = TimeSpan.MinValue;
            if (ctx.Snapshot.IsStashOpen)
            {
                ctx.Input.VerifiedTapKey(
                    VkEscape, ClickIntent.InteractUi, "close supply stash",
                    expectResolved: () => !(_getSnapshot()?.IsStashOpen ?? true),
                    timeoutMs: 2000);
                LastDecision = $"Supply: carrying {carried}; closing stash";
                return;
            }

            _device.Start(
                ctx.Entities, MapDeviceSystem.PayloadSource.InventorySimulacrum);
            _supplyCommittedToDevice = false;
            _supplyClickAttempts = 0;
            _entryTransition.Reset();
            _step = Step.Device;
            LastDecision = $"Supply/Device: carrying {carried} Simulacrum(s)";
            return;
        }

        if (!ctx.Snapshot.IsStashOpen)
        {
            _supplyPanelObservedAt = TimeSpan.MinValue;
            _openStash.Tick(ctx);
            LastDecision = $"Supply/OpenStash: {_openStash.LastDecision}";
            return;
        }

        var stash = ctx.Snapshot.StashInventory;
        var supplyTabName = ctx.Settings.SimulacrumSupplyTabName.Trim();
        if (supplyTabName.Length > 0)
        {
            var targetTab = ctx.Snapshot.StashTabs.Find(
                supplyTabName, requireGeneralPurpose: false);
            if (targetTab is null)
            {
                Stop($"supply stash tab '{supplyTabName}' not found");
                return;
            }
            if (stash.VisibleTabIndex != targetTab.DisplayIndex)
            {
                if (!_supplyTabSwitcher.IsStarted
                    || !_supplyTabSwitcher.TargetName.Equals(
                        supplyTabName, StringComparison.OrdinalIgnoreCase))
                    _supplyTabSwitcher.Start(supplyTabName, requireGeneralPurpose: false);
                var switchResult = _supplyTabSwitcher.Tick(ctx);
                LastDecision = $"Supply/SwitchTab: {_supplyTabSwitcher.Status}";
                if (switchResult == StashTabSwitcher.Result.Failed)
                    Stop($"supply-tab switch failed: {_supplyTabSwitcher.Status}");
                return;
            }
            if (_supplyTabSwitcher.IsStarted)
            {
                _supplyTabSwitcher.Reset();
                _supplyPanelObservedAt = BotMonotonicClock.Now;
                LastDecision = "Supply: Deli tab selected; settling specialized layout";
                return;
            }
        }
        if (_supplyPanelObservedAt == TimeSpan.MinValue)
        {
            _supplyPanelObservedAt = BotMonotonicClock.Now;
            LastDecision = "Supply: stash visible; settling specialized tab layout";
            return;
        }
        if (BotMonotonicClock.ElapsedSince(_supplyPanelObservedAt).TotalMilliseconds < 1500)
        {
            LastDecision = "Supply: waiting for specialized stash layout to settle";
            return;
        }
        var target = stash.Items.FirstOrDefault(item => item.Path.Contains(
            InventoryView.SimulacrumPathFragment, StringComparison.OrdinalIgnoreCase));
        if (target.ItemEntity == 0 || target.Rect is null)
        {
            _supplyMissingSince = _supplyMissingSince == TimeSpan.MinValue
                ? BotMonotonicClock.Now
                : _supplyMissingSince;
            if (BotMonotonicClock.ElapsedSince(_supplyMissingSince).TotalSeconds >= 2)
                Stop($"no Simulacrum stack on visible stash tab {stash.VisibleTabIndex}; "
                    + "select the Deli tab or restock supplies");
            else
                LastDecision = "Supply: waiting for visible Deli-tab Simulacrum stack";
            return;
        }

        if (_supplyClickAttempts >= MaxSupplyClicks)
        {
            Stop($"failed to withdraw Simulacrum stack after {MaxSupplyClicks} attempts");
            return;
        }
        if (BotMonotonicClock.ElapsedSince(_lastSupplyActionAt).TotalMilliseconds < 600)
            return;

        var rect = target.Rect.Value;
        _supplyTarget = $"tab={stash.VisibleTabIndex} stack={target.StackSize} "
            + $"rect={rect.X:F0},{rect.Y:F0},{rect.Width:F0},{rect.Height:F0}";
        var (x, y) = ctx.Snapshot.Window.ToScreen(
            (int)rect.CenterX, (int)rect.CenterY);
        var ticket = ctx.Input.ModifierClick(
            x, y, [VkLeftControl], ClickIntent.InteractUi,
            "withdraw Simulacrum stack",
            expectResolved: () => CountCarriedSupplies(_getSnapshot()?.Inventory) > 0,
            timeoutMs: 2000);
        if (ticket.Accepted)
        {
            _supplyClickAttempts++;
            _lastSupplyActionAt = BotMonotonicClock.Now;
            LastDecision = $"Supply: ctrl-clicked stack={target.StackSize} from tab {stash.VisibleTabIndex}";
            Diagnostics.EventLog.Emit(
                "simulacrum", "simulacrum.supply-withdraw-requested",
                Diagnostics.EventSeverity.Info, LastDecision,
                new Dictionary<string, object?>
                {
                    ["stack"] = target.StackSize,
                    ["tabIndex"] = stash.VisibleTabIndex,
                    ["path"] = target.Path,
                });
        }
    }

    private void TickDevice(BehaviorContext ctx)
    {
        if (ctx.Snapshot.Inventory.IsOpen)
        {
            _lastKnownInventoryCells = ctx.Snapshot.Inventory.OccupiedCells;
            _lastKnownInventoryCellsKnown = true;
            _carriedSupplies = CountCarriedSupplies(ctx.Snapshot.Inventory);
        }

        if (_entryTransition.State.Outcome != AreaTransitionOutcome.Idle)
        {
            var transition = _entryTransition.Observe(
                ctx.Snapshot.AreaHash,
                WorldAreaClassifier.Classify(ctx),
                AreaTransitionTracker.MonotonicNow());
            if (transition.Outcome == AreaTransitionOutcome.Confirmed
                || ShouldAttachFreshUnknownMap(transition))
            {
                StartArena(ctx.Snapshot.AreaHash);
                LastDecision = transition.Outcome == AreaTransitionOutcome.Confirmed
                    ? "Device/Arena: entered and verified Simulacrum area"
                    : "Device/Arena: fresh portal changed area; locating Simulacrum monolith";
                if (transition.ObservedRole == AreaRole.Unknown)
                {
                    Diagnostics.EventLog.Emit(
                        "simulacrum", "simulacrum.entry-empty-bubble",
                        Diagnostics.EventSeverity.Info, LastDecision,
                        new Dictionary<string, object?>
                        {
                            ["originAreaHash"] = $"0x{transition.OriginAreaHash:X8}",
                            ["observedAreaHash"] = $"0x{transition.ObservedAreaHash:X8}",
                        });
                }
                return;
            }
            if (ctx.Snapshot.AreaHash != 0
                && ctx.Snapshot.AreaHash != transition.OriginAreaHash
                && transition.Outcome is AreaTransitionOutcome.WaitingForChange
                    or AreaTransitionOutcome.VerifyingDestination)
            {
                // The destination snapshot arrives before its nearby entities sometimes do.
                // Do not let the hideout-side device controller interpret the missing old
                // portal as a failure while destination-role evidence is still hydrating.
                LastDecision = $"Device/Transition: entered area 0x{ctx.Snapshot.AreaHash:X8}; verifying destination";
                return;
            }
            if (transition.Outcome is AreaTransitionOutcome.UnexpectedDestination
                or AreaTransitionOutcome.TimedOut)
            {
                Stop($"Simulacrum entry {transition.Outcome}: observed {transition.ObservedRole}");
                return;
            }
        }

        var result = _device.Tick(ctx);
        if (!_supplyCommittedToDevice
            && _device.CurrentPhase is MapDeviceSystem.Phase.Activate
                or MapDeviceSystem.Phase.WaitForPortals
                or MapDeviceSystem.Phase.EnterPortal)
        {
            // Simulacrums are unstackable in player inventory. Once the visible device slot
            // positively contains it, the carried item and its one occupied cell are gone.
            _supplyCommittedToDevice = true;
            _carriedSupplies = Math.Max(0, _carriedSupplies - 1);
            if (_lastKnownInventoryCellsKnown)
                _lastKnownInventoryCells = Math.Max(0, _lastKnownInventoryCells - 1);
            Diagnostics.EventLog.Emit(
                "simulacrum", "simulacrum.supply-staged",
                Diagnostics.EventSeverity.Info,
                "Simulacrum committed to visible map-device slot",
                new Dictionary<string, object?>
                {
                    ["carriedRemaining"] = _carriedSupplies,
                    ["inventoryCellsEstimate"] = _lastKnownInventoryCells,
                });
        }
        if (result == MapDeviceSystem.Result.Failed)
        {
            if (!_deviceStorageChecked && _device.Status.Contains("no Simulacrum"))
            {
                _deviceStorageChecked = true;
                _step = Step.Supply;
                _device.Cancel();
                LastDecision = "Device/Supply: device storage empty, falling back to stash";
                return;
            }

            var role = WorldAreaClassifier.Classify(ctx);
            if (_device.Status.Contains("entity disappeared") && role != AreaRole.SafeHub)
            {
                // We misclicked a portal while trying to interact with the map device.
                _step = Step.Recover;
                _recoveryLeg = RecoveryLeg.ExitArena;
                _recoveryOriginAreaHash = ctx.Snapshot.AreaHash;
                _recoveryStartedAt = AreaTransitionTracker.MonotonicNow();
                _returnThroughPortal.Reset();
                _discardExistingRun = true;
                _recoveryReason = "accidental-portal-click";
                LastDecision = "Device: accidental portal click detected; exiting to hideout";
                return;
            }

            Stop($"Simulacrum device failed: {_device.Status}");
            return;
        }
        if (_device.CurrentPhase == MapDeviceSystem.Phase.EnterPortal
            && _entryTransition.State.Outcome == AreaTransitionOutcome.Idle)
        {
            _entryTransition.Start(
                ctx.Snapshot.AreaHash, AreaRole.SafeHub, AreaRole.Map,
                AreaTransitionTracker.MonotonicNow());
        }
        LastDecision = $"Device/{_device.CurrentPhase}: {_device.Status}";
    }

    private void TickArena(GameSnapshot snapshot, IInputRouter input, BehaviorContext ctx)
    {
        if (_arena.Phase == SimulacrumPhase.Terminal
            && snapshot.AreaHash != 0
            && snapshot.AreaHash != _arenaAreaHash)
        {
            var role = WorldAreaClassifier.Classify(ctx);
            if (role != AreaRole.SafeHub)
            {
                Stop($"Simulacrum exit reached unexpected destination {role}");
                return;
            }
            _runsCompleted++;
            Diagnostics.EventLog.Emit(
                "simulacrum", "simulacrum.run-completed",
                Diagnostics.EventSeverity.Info,
                $"completed Simulacrum {_runsCompleted}; returned to hideout",
                new Dictionary<string, object?> { ["runsCompleted"] = _runsCompleted });
            if (_settings.Current.SimulacrumTargetRuns > 0
                && _runsCompleted >= _settings.Current.SimulacrumTargetRuns)
            {
                Stop($"target Simulacrum count reached ({_runsCompleted})");
                return;
            }
            _arena.Reset();
            _device.Cancel();
            _entryTransition.Reset();
            _exitTransition.Reset();
            _step = Step.Device;
            _deviceStorageChecked = false;
            _device.Start(ctx.Entities, MapDeviceSystem.PayloadSource.InventorySimulacrum);
            LastDecision = $"Arena/Device: run {_runsCompleted} complete, checking map device storage";
            return;
        }

        if (snapshot.AreaHash != _arenaAreaHash)
        {
            // The area hash can briefly read as zero/stale while a checkpoint revive swaps
            // top-level game states.  Treat that as transition evidence, not as proof that
            // the character left the arena.  A real premature departure must remain in a
            // positively classified destination before it is allowed to latch the run stop.
            var now = AreaTransitionTracker.MonotonicNow();
            if (_arenaMismatchSince == TimeSpan.MinValue)
                _arenaMismatchSince = now;
            var role = WorldAreaClassifier.Classify(ctx);
            var elapsed = now - _arenaMismatchSince;
            if (role == AreaRole.Unknown || elapsed < ArenaDepartureConfirmation)
            {
                LastDecision = $"Arena/Transition: awaiting stable destination ({role}, {elapsed.TotalMilliseconds:F0}ms)";
                return;
            }

            if (role == AreaRole.SafeHub && _deathRecoveryPending)
            {
                if (_runDeaths > _settings.Current.SimulacrumMaxDeaths)
                {
                    Stop($"Simulacrum death budget exceeded ({_runDeaths}/{_settings.Current.SimulacrumMaxDeaths})");
                    return;
                }
                _step = Step.Recover;
                _recoveryStartedAt = now;
                _recoveryLeg = RecoveryLeg.ReturnToArena;
                _recoveryReason = $"checkpoint death {_runDeaths}";
                _recoveryOriginAreaHash = _arenaAreaHash;
                _returnThroughPortal.Reset();
                LastDecision = $"Recovery: returned to checkpoint after death {_runDeaths}; locating existing portal";
                Diagnostics.EventLog.Emit(
                    "simulacrum", "simulacrum.death-recovery-started",
                    Diagnostics.EventSeverity.Warning, LastDecision,
                    new Dictionary<string, object?> { ["runDeaths"] = _runDeaths });
                return;
            }

            Stop($"left Simulacrum before terminal completion (destination {role})");
            return;
        }

        _arenaMismatchSince = TimeSpan.MinValue;

        _arena.Tick(snapshot, input);
        if (_arena.PortalRefreshRequested)
        {
            _step = Step.Recover;
            _recoveryStartedAt = AreaTransitionTracker.MonotonicNow();
            _recoveryLeg = RecoveryLeg.ExitArena;
            _recoveryReason = $"wave {_arena.ActiveWave} dry-sweep portal refresh";
            _recoveryOriginAreaHash = snapshot.AreaHash;
            _returnThroughPortal.Reset();
            LastDecision = "Recovery/Exit: dry-sweep budget exhausted; leaving and re-entering same instance";
            Diagnostics.EventLog.Emit(
                "simulacrum", "simulacrum.portal-refresh-started",
                Diagnostics.EventSeverity.Warning, LastDecision,
                new Dictionary<string, object?>
                {
                    ["wave"] = _arena.ActiveWave,
                    ["completedSweeps"] = _arena.WaveSweepPass,
                    ["areaHash"] = $"0x{snapshot.AreaHash:X8}",
                });
            return;
        }
        if (_arena.IsFailed)
        {
            Stop($"Simulacrum arena failed: {_arena.FailureReason}");
            return;
        }
        if (_arena.Phase == SimulacrumPhase.Terminal
            && _exitTransition.State.Outcome == AreaTransitionOutcome.Idle)
        {
            _exitTransition.Start(
                snapshot.AreaHash, AreaRole.Map, AreaRole.SafeHub,
                AreaTransitionTracker.MonotonicNow());
        }
        LastDecision = $"Arena/{_arena.Phase}: {_arena.LastDecision}";
    }

    private void TickRecovery(BehaviorContext ctx)
    {
        if (_runDeaths > _settings.Current.SimulacrumMaxDeaths)
        {
            Stop($"Simulacrum death budget exceeded ({_runDeaths}/{_settings.Current.SimulacrumMaxDeaths})");
            return;
        }
        if (_recoveryStartedAt != TimeSpan.MinValue
            && AreaTransitionTracker.MonotonicNow() - _recoveryStartedAt > TimeSpan.FromSeconds(30))
        {
            Stop($"Simulacrum recovery timed out during {_recoveryLeg}: {_recoveryReason}");
            return;
        }

        // The portal-entered latch only has meaning while returning to the arena.
        if (_recoveryLeg != RecoveryLeg.ReturnToArena) _reentryPortalEntered = false;

        if (_recoveryLeg == RecoveryLeg.AwaitSafeHub)
        {
            var role = WorldAreaClassifier.Classify(ctx);
            if (ctx.Snapshot.AreaHash != 0
                && ctx.Snapshot.AreaHash != _recoveryOriginAreaHash
                && role == AreaRole.SafeHub)
            {
                if (_discardExistingRun)
                {
                    CompleteDiscardToSupply(ctx.Snapshot.AreaHash);
                    return;
                }
                _recoveryLeg = RecoveryLeg.ReturnToArena;
                _recoveryStartedAt = AreaTransitionTracker.MonotonicNow();
                _returnThroughPortal.Reset();
                LastDecision = $"Recovery/Return: hideout confirmed after {_recoveryReason}; entering existing portal";
                Diagnostics.EventLog.Emit(
                    "simulacrum", "simulacrum.portal-refresh-hideout-confirmed",
                    Diagnostics.EventSeverity.Info, LastDecision,
                    new Dictionary<string, object?>
                    {
                        ["areaHash"] = $"0x{ctx.Snapshot.AreaHash:X8}",
                        ["role"] = role.ToString(),
                    });
                return;
            }
            LastDecision = $"Recovery/AwaitSafeHub: waiting for stable hideout evidence ({role})";
            return;
        }

        if (_recoveryLeg == RecoveryLeg.ExitArena)
        {
            var result = _returnThroughPortal.Tick(ctx);
            if (result == BehaviorStatus.Success)
            {
                _recoveryLeg = RecoveryLeg.AwaitSafeHub;
                _recoveryStartedAt = AreaTransitionTracker.MonotonicNow();
                _returnThroughPortal.Reset();
                LastDecision = $"Recovery/AwaitSafeHub: exited arena for {_recoveryReason}; confirming hideout";
                Diagnostics.EventLog.Emit(
                    "simulacrum", "simulacrum.portal-refresh-exited",
                    Diagnostics.EventSeverity.Info, LastDecision);
                return;
            }
            LastDecision = $"Recovery/Exit: locating arena portal for {_recoveryReason}";
            return;
        }

        // RecoveryLeg.ReturnToArena — two-phase: (1) click the existing portal, (2) WAIT for the
        // arena area to actually load before reattaching. Latching the still-hideout hash here is
        // the wave-11 hang bug; only reattach once we're positively in a changed, non-hub area.
        if (!_reentryPortalEntered)
        {
            if (_returnThroughPortal.Tick(ctx) != BehaviorStatus.Success)
            {
                LastDecision = $"Recovery/Return: locating and entering existing portal after {_recoveryReason}";
                return;
            }
            _reentryPortalEntered = true;
            _reentryAwaitStartedAt = AreaTransitionTracker.MonotonicNow();
            _returnThroughPortal.Reset();
            LastDecision = "Recovery/Return: portal entered; awaiting arena load";
            return;
        }

        var arenaRole = WorldAreaClassifier.Classify(ctx);
        if (ctx.Snapshot.AreaHash == 0
            || ctx.Snapshot.AreaHash != _arenaAreaHash
            || arenaRole == AreaRole.SafeHub)
        {
            if (_reentryAwaitStartedAt != TimeSpan.MinValue
                && AreaTransitionTracker.MonotonicNow() - _reentryAwaitStartedAt > TimeSpan.FromSeconds(5))
            {
                // The portal click was a false positive, or the instance join failed, leaving us in Hideout.
                // Reset the portal state to force the behavior to try clicking another portal.
                _reentryPortalEntered = false;
                LastDecision = $"Recovery/Return: portal transition failed after 5s; retrying";
                return;
            }

            LastDecision = $"Recovery/Return: awaiting arena (area 0x{ctx.Snapshot.AreaHash:X8}, {arenaRole})";
            return;
        }

        var reason = _recoveryReason;
        ReattachArena(
            ctx.Snapshot.AreaHash,
            preserveSweepProgress: reason.StartsWith(
                "checkpoint death", StringComparison.OrdinalIgnoreCase));
        _reentryPortalEntered = false;
        LastDecision = $"Recovery/Arena: re-entered existing instance after {reason}";
        Diagnostics.EventLog.Emit(
            "simulacrum", "simulacrum.portal-recovery-completed",
            Diagnostics.EventSeverity.Info, LastDecision,
            new Dictionary<string, object?>
            {
                ["runDeaths"] = _runDeaths,
                ["reason"] = reason,
                ["areaHash"] = $"0x{ctx.Snapshot.AreaHash:X8}",
            });
    }

    private void CompleteDiscardToSupply(uint hideoutAreaHash)
    {
        _settings.Mutate(settings => settings.SimulacrumDiscardExistingRun = false);
        _arena.Reset();
        _device.Cancel();
        _entryTransition.Reset();
        _exitTransition.Reset();
        _arenaAreaHash = 0;
        _arenaMismatchSince = TimeSpan.MinValue;
        _deathRecoveryPending = false;
        _runDeaths = 0;
        _recoveryStartedAt = TimeSpan.MinValue;
        _recoveryLeg = RecoveryLeg.ReturnToArena;
        _recoveryReason = string.Empty;
        _recoveryOriginAreaHash = 0;
        _returnThroughPortal.Reset();
        _discardExistingRun = false;
        _step = Step.Device;
        _deviceStorageChecked = false;
        _device.Start(null!, MapDeviceSystem.PayloadSource.InventorySimulacrum);
        LastDecision = "Discard/Device: successfully discarded portal, checking map device storage";
        Diagnostics.EventLog.Emit(
            "simulacrum", "simulacrum.discard-completed",
            Diagnostics.EventSeverity.Info, LastDecision,
            new Dictionary<string, object?>
            {
                ["hideoutAreaHash"] = $"0x{hideoutAreaHash:X8}",
            });
    }

    private void StartArena(uint areaHash)
    {
        _device.Cancel();
        _arena.Reset();
        _arenaMismatchSince = TimeSpan.MinValue;
        if (_lastKnownInventoryCellsKnown)
            _arena.SeedInventoryOccupancy(_lastKnownInventoryCells);
        _arenaAreaHash = areaHash;
        _runDeaths = 0;
        _deathRecoveryPending = false;
        _recoveryStartedAt = TimeSpan.MinValue;
        _recoveryLeg = RecoveryLeg.ReturnToArena;
        _recoveryReason = string.Empty;
        _recoveryOriginAreaHash = 0;
        _runsStarted++;
        _step = Step.Arena;
    }

    private void ReattachArena(uint areaHash, bool preserveSweepProgress = false)
    {
        _arena.ResetForReattach(preserveSweepProgress);
        if (_lastKnownInventoryCellsKnown)
            _arena.SeedInventoryOccupancy(_lastKnownInventoryCells);
        _arena.SeedDeathCount(_runDeaths);
        _arenaAreaHash = areaHash;
        _arenaMismatchSince = TimeSpan.MinValue;
        _deathRecoveryPending = false;
        _recoveryStartedAt = TimeSpan.MinValue;
        _recoveryLeg = RecoveryLeg.ReturnToArena;
        _recoveryReason = string.Empty;
        _recoveryOriginAreaHash = 0;
        _returnThroughPortal.Reset();
        _step = Step.Arena;
    }

    private void Stop(string reason)
    {
        _stopped = true;
        _step = Step.Stopped;
        _stopReason = reason;
        _movement.Release();
        _interact.Cancel();
        _device.Cancel();
        _settings.Mutate(settings => settings.BotActive = false);
        LastDecision = $"STOPPED: {reason}";
        Diagnostics.EventLog.Emit(
            "simulacrum", "simulacrum.loop-stopped",
            Diagnostics.EventSeverity.Warning, reason,
            new Dictionary<string, object?>
            {
                ["runsStarted"] = _runsStarted,
                ["runsCompleted"] = _runsCompleted,
            });
    }

    private static int CountCarriedSupplies(InventoryView? inventory)
    {
        if (inventory is null || !inventory.IsOpen) return 0;
        var total = 0;
        foreach (var item in inventory.Items)
            if (item.Path.Contains(
                    InventoryView.SimulacrumPathFragment,
                    StringComparison.OrdinalIgnoreCase))
                total += Math.Max(1, item.StackSize);
        return total;
    }

    private static EntityCache.Entry? FindStash(BehaviorContext ctx)
        => ctx.Entities?.Entries.Values.FirstOrDefault(entity =>
            !entity.IsStale && entity.Kind == EntityListReader.EntityKind.Stash);

    private static EntityCache.Entry? FindExistingPortal(BehaviorContext ctx)
        => ctx.Entities?.Entries.Values.FirstOrDefault(entity =>
            !entity.IsStale && entity.Kind is EntityListReader.EntityKind.TownPortal
                or EntityListReader.EntityKind.Portal);

    private static EntityCache.Entry? FindMonolith(BehaviorContext ctx)
        => ctx.Entities?.Entries.Values.FirstOrDefault(entity =>
            !entity.IsStale
            && entity.Path.Contains("Objects/Afflictionator", StringComparison.OrdinalIgnoreCase));
}
