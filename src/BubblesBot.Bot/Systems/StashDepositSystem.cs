using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Input;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Dumps inventory loot into the currently-open stash tab. Phases: walk to the stash entity →
/// click it open → Ctrl+click each depositable item into the open tab → done when nothing
/// depositable remains. Mirrors <see cref="MapDeviceSystem"/> / <see cref="LeaveMapSystem"/>
/// so the stacked-deck orchestrator drives all three the same way.
///
/// <para><b>Retain rule (v1).</b> Mapping supplies are never deposited — currently just Portal
/// Scrolls (<see cref="InventoryView.IsRetainedSupply"/>), which fuel the F-key map exit. Refine
/// to "keep ≥ N, deposit excess" later.</para>
///
/// <para><b>Verify + blacklist.</b> PoE Ctrl+click moves the hovered item to the open stash
/// tab; we confirm by the item entity vanishing from the inventory on the next read. An item
/// that won't move after <see cref="MaxItemAttempts"/> tries is blacklisted (full tab, wrong
/// tab type, unstashable) so the flow doesn't wedge — same shape as <c>LootClosestVisible</c>.</para>
/// </summary>
public sealed class StashDepositSystem
{
    public enum Phase { Idle, NavigateToStash, OpenStash, SwitchTab, Deposit, Done, Failed }
    public enum Result { InProgress, Succeeded, Failed }

    public Phase CurrentPhase { get; private set; } = Phase.Idle;
    public string Status { get; private set; } = "idle";
    public bool IsBusy => CurrentPhase is not (Phase.Idle or Phase.Done or Phase.Failed);

    /// <summary>Items moved into the stash this run. Reset on <see cref="Start"/>.</summary>
    public int Deposited { get; private set; }
    /// <summary>Depositable items still observed in inventory, including blocked items.</summary>
    public int Remaining { get; private set; }

    private readonly MovementSystem        _movement;
    private readonly Func<GameSnapshot?>   _getSnapshot;
    private readonly Func<InventoryView, InventoryView.Item, bool> _retainItem;
    private readonly FollowPath            _approach;
    private readonly StashTabSwitcher      _tabSwitcher;

    private string   _targetTabName = string.Empty;
    private TimeSpan _phaseStartedAt;
    private TimeSpan _lastActionAt;
    private int      _clickAttempts;          // open-stash clicks
    private int      _itemAttempts;           // ctrl+clicks on the current item
    private nint     _currentItemEntity;
    private int      _lastDepositableCount = -1;
    private readonly HashSet<nint> _blacklist = new();   // item entity addrs that won't stash

    private const int ActionCooldownMs    = 450;
    private const int MaxOpenClicks       = 5;
    private const int MaxItemAttempts     = 3;
    private const int PhaseTimeoutSeconds = 60;
    private const int VK_LCONTROL         = 0xA2;

    public StashDepositSystem(
        MovementSystem movement,
        SkillBook skills,
        Func<GameSnapshot?> getSnapshot,
        Func<InventoryView, InventoryView.Item, bool>? retainItem = null)
    {
        _movement    = movement;
        _getSnapshot = getSnapshot;
        _retainItem  = retainItem ?? ((_, item) => InventoryView.IsRetainedSupply(item));
        _approach    = new FollowPath("stash/approach", movement, GetStashGoal, skills);
        _tabSwitcher = new StashTabSwitcher(getSnapshot);
    }

    public void Start(string? targetTabName = null)
    {
        _targetTabName = targetTabName?.Trim() ?? string.Empty;
        CurrentPhase    = Phase.NavigateToStash;
        _phaseStartedAt = BotMonotonicClock.Now;
        _lastActionAt   = TimeSpan.Zero;
        _clickAttempts  = 0;
        _itemAttempts   = 0;
        _currentItemEntity = 0;
        _lastDepositableCount = -1;
        Deposited = 0;
        Remaining = 0;
        _blacklist.Clear();
        _approach.Reset();
        _tabSwitcher.Reset();
        Status = "navigating to stash";
        BubblesBot.Bot.Diagnostics.EventLog.Log("Stash", "deposit flow started");
    }

    public void Cancel()
    {
        CurrentPhase = Phase.Idle;
        _movement.Release();
        _approach.Reset();
        _tabSwitcher.Reset();
        Status = "cancelled";
    }

    public Result Tick(BehaviorContext ctx)
    {
        if (!IsBusy)
            return CurrentPhase == Phase.Done   ? Result.Succeeded
                 : CurrentPhase == Phase.Failed ? Result.Failed
                 : Result.InProgress;

        if ((BotMonotonicClock.Now - _phaseStartedAt).TotalSeconds > PhaseTimeoutSeconds)
            return Fail($"timeout in {CurrentPhase}: {Status}");

        if ((BotMonotonicClock.Now - _lastActionAt).TotalMilliseconds < ActionCooldownMs)
            return Result.InProgress;

        return CurrentPhase switch
        {
            Phase.NavigateToStash => TickNavigate(ctx),
            Phase.OpenStash       => TickOpen(ctx),
            Phase.SwitchTab       => TickSwitchTab(ctx),
            Phase.Deposit         => TickDeposit(ctx),
            _ => Result.InProgress,
        };
    }

    // ─── Phases ──────────────────────────────────────────────────────────

    private Result TickNavigate(BehaviorContext ctx)
    {
        var stash = FindStash(ctx);
        if (stash is null) { Status = "no stash entity in area"; return Result.InProgress; }
        if (ctx.Live is null) { Status = "no live player"; return Result.InProgress; }

        var dist = Distance(ctx.Live.Value.GridPosition, stash.GridPosition);
        if (dist <= ctx.Settings.InteractionRangeGrid)
        {
            _movement.Release();
            return Advance(Phase.OpenStash, "in range — opening stash");
        }

        _approach.Tick(ctx);
        Status = $"approaching stash (dist={dist:F0})";
        return Result.InProgress;
    }

    private Result TickOpen(BehaviorContext ctx)
    {
        if (ctx.Snapshot.IsStashOpen)
        {
            _clickAttempts = 0;
            if (_targetTabName.Length > 0)
            {
                _tabSwitcher.Start(_targetTabName, requireGeneralPurpose: true);
                return Advance(Phase.SwitchTab, $"stash open - switching to '{_targetTabName}'");
            }
            return Advance(Phase.Deposit, "stash open - depositing");
        }

        if (_clickAttempts >= MaxOpenClicks)
            return Fail($"failed to open stash after {MaxOpenClicks} clicks");

        var stash = FindStash(ctx);
        if (stash is null) return Fail("stash entity disappeared");

        var clickPoint = EntityClick.ResolveScreenPoint(ctx, stash);
        if (clickPoint is null) { Status = "no stash click point"; return Result.InProgress; }

        var ticket = ctx.Input.Click(clickPoint.Value.X, clickPoint.Value.Y,
            ClickIntent.InteractWorld, "open stash",
            expectResolved: () => _getSnapshot()?.IsStashOpen ?? false, timeoutMs: 2500);
        if (ticket.Accepted)
        {
            _clickAttempts++;
            _lastActionAt = BotMonotonicClock.Now;
            Status = $"clicked stash ({_clickAttempts}/{MaxOpenClicks})";
        }
        return Result.InProgress;
    }

    private Result TickSwitchTab(BehaviorContext ctx)
    {
        var result = _tabSwitcher.Tick(ctx);
        Status = _tabSwitcher.Status;
        return result switch
        {
            StashTabSwitcher.Result.Succeeded => Advance(
                Phase.Deposit, $"on '{_targetTabName}' - depositing"),
            StashTabSwitcher.Result.Failed => Fail(
                $"dump-tab switch failed: {_tabSwitcher.Status}"),
            _ => Result.InProgress,
        };
    }

    private Result TickDeposit(BehaviorContext ctx)
    {
        if (!ctx.Snapshot.IsStashOpen)
            return Fail("stash closed mid-deposit");

        var inv = ctx.Snapshot.Inventory;
        if (!inv.IsOpen) { Status = "inventory not readable"; return Result.InProgress; }

        // Count what's left to deposit; credit any items that drained since last tick.
        var depositable = CountDepositable(inv);
        Remaining = depositable;
        if (_lastDepositableCount >= 0 && depositable < _lastDepositableCount)
            Deposited += _lastDepositableCount - depositable;
        _lastDepositableCount = depositable;

        // Pick the next depositable item not already blacklisted and with a usable rect.
        InventoryView.Item? target = null;
        foreach (var it in inv.Items)
        {
            if (_retainItem(inv, it)) continue;
            if (_blacklist.Contains(it.ItemEntity)) continue;
            if (it.Rect is null) continue;
            target = it;
            break;
        }

        if (target is null)
        {
            var blocked = 0;
            var unreadableRect = 0;
            foreach (var item in inv.Items)
            {
                if (_retainItem(inv, item)) continue;
                if (_blacklist.Contains(item.ItemEntity)) blocked++;
                else if (item.Rect is null) unreadableRect++;
            }
            var assessment = DepositOutcomeEvaluator.Evaluate(
                depositable, actionable: 0, blocked, unreadableRect);
            if (assessment.Outcome == DepositOutcome.Complete)
            {
                BubblesBot.Bot.Diagnostics.EventLog.Emit(
                    "stash", "stash.deposit.completed", BubblesBot.Bot.Diagnostics.EventSeverity.Info,
                    $"deposit complete - {Deposited} items moved",
                    new Dictionary<string, object?> { ["deposited"] = Deposited, ["remaining"] = 0 });
                return Advance(Phase.Done, $"deposited {Deposited} items");
            }

            return Fail($"{depositable} items remain but none are actionable " +
                        $"(blacklisted={blocked}, missingRect={unreadableRect})");
        }

        // New target → reset its attempt counter.
        if (target.Value.ItemEntity != _currentItemEntity)
        {
            _currentItemEntity = target.Value.ItemEntity;
            _itemAttempts = 0;
        }

        if (_itemAttempts >= MaxItemAttempts)
        {
            _blacklist.Add(_currentItemEntity);
            BubblesBot.Bot.Diagnostics.EventLog.Log("Stash",
                $"blacklisted item 0x{(long)_currentItemEntity:X} (won't stash after {MaxItemAttempts} tries)");
            _currentItemEntity = 0;
            return Result.InProgress;
        }

        var rect = target.Value.Rect!.Value;
        var (sx, sy) = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
        var itemEntity = target.Value.ItemEntity;
        var ticket = ctx.Input.ModifierClick(sx, sy, new[] { VK_LCONTROL }, ClickIntent.InteractUi,
            "deposit item",
            expectResolved: () =>
            {
                var s = _getSnapshot();
                if (s is null) return false;
                foreach (var i in s.Inventory.Items)
                    if (i.ItemEntity == itemEntity) return false;   // still present → not resolved
                return true;
            },
            timeoutMs: 1500);
        if (ticket.Accepted)
        {
            _itemAttempts++;
            _lastActionAt = BotMonotonicClock.Now;
            Status = $"ctrl+click deposit ({Deposited} done, {depositable} left)";
        }
        return Result.InProgress;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private int CountDepositable(InventoryView inv)
    {
        var n = 0;
        foreach (var it in inv.Items)
            if (!_retainItem(inv, it))
                n++;
        return n;
    }

    private Result Advance(Phase next, string status)
    {
        BubblesBot.Bot.Diagnostics.EventLog.Log("Stash", $"phase {CurrentPhase} → {next}: {status}");
        CurrentPhase    = next;
        _phaseStartedAt = BotMonotonicClock.Now;
        _lastActionAt   = TimeSpan.Zero;
        Status          = status;
        return Result.InProgress;
    }

    private Result Fail(string reason)
    {
        BubblesBot.Bot.Diagnostics.EventLog.Emit(
            "stash", "stash.deposit.failed", BubblesBot.Bot.Diagnostics.EventSeverity.Error,
            reason,
            new Dictionary<string, object?>
            {
                ["phase"] = CurrentPhase.ToString(),
                ["deposited"] = Deposited,
                ["remaining"] = Remaining,
                ["blacklisted"] = _blacklist.Count,
            });
        CurrentPhase = Phase.Failed;
        Status = reason;
        _movement.Release();
        return Result.Failed;
    }

    private Vector2i? GetStashGoal(BehaviorContext ctx) => FindStash(ctx)?.GridPosition;

    private static EntityCache.Entry? FindStash(BehaviorContext ctx)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var p = ctx.Live.Value.GridPosition;
        EntityCache.Entry? best = null;
        long bestD2 = long.MaxValue;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (e.Kind != EntityListReader.EntityKind.Stash) continue;
            long dx = e.GridPosition.X - p.X;
            long dy = e.GridPosition.Y - p.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = e; }
        }
        return best;
    }

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
