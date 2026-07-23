using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Input;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>Moves between named stash tabs with arrow keys and visible-index confirmation.</summary>
public sealed class StashTabSwitcher
{
    public enum Result { InProgress, Succeeded, Failed }

    private const int VkLeft = 0x25;
    private const int VkRight = 0x27;
    private const int MaxMoves = 128;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly Func<GameSnapshot?> _getSnapshot;
    private string _targetName = string.Empty;
    private bool _requireGeneralPurpose;
    private TimeSpan _startedAt;
    private TimeSpan _lastActionAt = TimeSpan.MinValue;
    private int _moves;

    public string TargetName => _targetName;
    public string Status { get; private set; } = "idle";
    public bool IsStarted => _targetName.Length > 0;

    public StashTabSwitcher(Func<GameSnapshot?> getSnapshot)
        => _getSnapshot = getSnapshot;

    public void Start(string targetName, bool requireGeneralPurpose)
    {
        _targetName = targetName.Trim();
        _requireGeneralPurpose = requireGeneralPurpose;
        _startedAt = BotMonotonicClock.Now;
        _lastActionAt = TimeSpan.MinValue;
        _moves = 0;
        Status = $"locating tab '{_targetName}'";
    }

    public void Reset()
    {
        _targetName = string.Empty;
        _moves = 0;
        Status = "idle";
    }

    public Result Tick(BehaviorContext ctx)
    {
        if (!ctx.Snapshot.IsStashOpen)
            return Fail("stash is closed");
        if (_targetName.Length == 0)
            return Fail("target tab is empty");
        if (BotMonotonicClock.ElapsedSince(_startedAt) > Timeout)
            return Fail($"timeout switching to '{_targetName}'");

        var target = ctx.Snapshot.StashTabs.Find(_targetName, _requireGeneralPurpose);
        if (target is null)
            return Fail(_requireGeneralPurpose
                ? $"general-purpose stash tab '{_targetName}' not found"
                : $"stash tab '{_targetName}' not found");

        var current = ctx.Snapshot.StashInventory.VisibleTabIndex;
        if (current == target.DisplayIndex)
        {
            Status = $"on tab '{target.Name}' index={target.DisplayIndex}";
            BubblesBot.Bot.Diagnostics.EventLog.Emit(
                "stash", "stash.tab-selected",
                BubblesBot.Bot.Diagnostics.EventSeverity.Info,
                Status,
                new Dictionary<string, object?>
                {
                    ["name"] = target.Name,
                    ["displayIndex"] = target.DisplayIndex,
                    ["type"] = target.Type,
                    ["generalPurposeRequired"] = _requireGeneralPurpose,
                });
            return Result.Succeeded;
        }
        if (current < 0)
        {
            Status = "waiting for visible stash index";
            return Result.InProgress;
        }
        if (_moves >= MaxMoves)
            return Fail($"move limit switching {current} -> {target.DisplayIndex}");
        if (BotMonotonicClock.ElapsedSince(_lastActionAt).TotalMilliseconds < 250)
            return Result.InProgress;

        var direction = current < target.DisplayIndex ? 1 : -1;
        var key = direction > 0 ? VkRight : VkLeft;
        var before = current;
        var ticket = ctx.Input.VerifiedTapKey(
            key, ClickIntent.InteractUi,
            $"switch stash tab toward '{target.Name}'",
            expectResolved: () =>
            {
                var live = _getSnapshot();
                return live is not null
                    && live.IsStashOpen
                    && live.StashInventory.VisibleTabIndex != before;
            },
            timeoutMs: 1500);
        if (ticket.Accepted)
        {
            _moves++;
            _lastActionAt = BotMonotonicClock.Now;
            Status = $"switching {before} -> {target.DisplayIndex} ({_moves}/{MaxMoves})";
        }
        return Result.InProgress;
    }

    private Result Fail(string reason)
    {
        Status = reason;
        return Result.Failed;
    }
}
