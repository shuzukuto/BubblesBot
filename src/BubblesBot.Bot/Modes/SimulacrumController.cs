using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

public enum SimulacrumPhase
{
    AwaitingWave,
    StartingWave,
    Fighting,
    Looting,
    Depositing,
    Terminal,
    Failed,
}

public enum SimulacrumCommand
{
    Wait,
    StartWave,
    Fight,
    Loot,
    Deposit,
    Leave,
    Stop,
}

public readonly record struct SimulacrumFrame(
    TimeSpan Now,
    int Wave,
    int Deaths,
    BooleanObservation StartReady,
    BooleanObservation WaveActive,
    BooleanObservation WaveComplete,
    BooleanObservation RewardsAvailable,
    bool InventoryNeedsDeposit);

public readonly record struct SimulacrumDecision(
    SimulacrumPhase Phase,
    SimulacrumCommand Command,
    string Reason);

/// <summary>
/// Memory-agnostic Simulacrum lifecycle. Critical observations fail closed; a future live
/// adapter supplies validated monolith/wave/reward observations and executes the command.
/// </summary>
public sealed class SimulacrumController
{
    public static bool CanRefreshPortal(int attempts, int maxAttempts)
        => attempts >= 0 && maxAttempts > 0 && attempts < maxAttempts;

    private readonly int _maxWaves;
    private readonly int _maxDeaths;
    private readonly TimeSpan _startTimeout;
    private readonly TimeSpan _waveTimeout;
    private TimeSpan _phaseStartedAt = TimeSpan.MinValue;

    public SimulacrumPhase Phase { get; private set; } = SimulacrumPhase.AwaitingWave;
    public string TerminalReason { get; private set; } = string.Empty;

    /// <summary>
    /// The active flag can flip one memory refresh before the wave counter advances. Correlate
    /// that edge with the last completed wave so the immediate start event is not mislabeled.
    /// Attaching mid-wave still trusts a larger already-advanced observed counter.
    /// </summary>
    public static int CorrelateStartedWave(int observedWave, int lastKnownWave, int maxWaves = 15)
        => Math.Clamp(Math.Max(observedWave, lastKnownWave + 1), 1, Math.Max(1, maxWaves));

    public SimulacrumController(
        int maxWaves = 15,
        int maxDeaths = 3,
        TimeSpan? startTimeout = null,
        TimeSpan? waveTimeout = null)
    {
        _maxWaves = Math.Max(1, maxWaves);
        _maxDeaths = Math.Max(0, maxDeaths);
        _startTimeout = startTimeout ?? TimeSpan.FromSeconds(12);
        _waveTimeout = waveTimeout ?? TimeSpan.FromMinutes(5);
    }

    public SimulacrumDecision Tick(SimulacrumFrame frame)
    {
        if (_phaseStartedAt == TimeSpan.MinValue) _phaseStartedAt = frame.Now;
        if (Phase is SimulacrumPhase.Terminal or SimulacrumPhase.Failed)
            return Decision(Phase == SimulacrumPhase.Terminal ? SimulacrumCommand.Leave : SimulacrumCommand.Stop,
                TerminalReason);

        if (frame.Deaths > _maxDeaths)
            return Fail(frame.Now, $"death budget exceeded ({frame.Deaths}/{_maxDeaths})");

        return Phase switch
        {
            SimulacrumPhase.AwaitingWave => TickAwaiting(frame),
            SimulacrumPhase.StartingWave => TickStarting(frame),
            SimulacrumPhase.Fighting => TickFighting(frame),
            SimulacrumPhase.Looting => TickLooting(frame),
            SimulacrumPhase.Depositing => TickDepositing(frame),
            _ => Decision(SimulacrumCommand.Wait, "unsupported phase"),
        };
    }

    public void Reset()
    {
        Phase = SimulacrumPhase.AwaitingWave;
        TerminalReason = string.Empty;
        _phaseStartedAt = TimeSpan.MinValue;
    }

    /// <summary>
    /// Reattach can occur after a completed wave but before its rewards were collected. Enter
    /// the normal reward phase so the adapter returns to the drop anchor and observes a full
    /// quiet window before allowing another start.
    /// </summary>
    public void ResumeBetweenWaves(TimeSpan now)
    {
        if (Phase == SimulacrumPhase.AwaitingWave)
            Transition(SimulacrumPhase.Looting, now);
    }

    /// <summary>
    /// Refresh the no-progress deadline while the adapter is making bounded physical
    /// progress (for example, completing a full arena sweep while the monolith remains
    /// active). The wave timeout remains a stall detector rather than a total-duration cap;
    /// the bot-wide maximum runtime is the outer hard limit.
    /// </summary>
    public void ReportFightProgress(TimeSpan now)
    {
        if (Phase == SimulacrumPhase.Fighting)
            _phaseStartedAt = now;
    }

    private SimulacrumDecision TickAwaiting(SimulacrumFrame frame)
    {
        // Reattach/re-arm can occur after a wave already started. Positive active evidence
        // resumes combat directly instead of waiting forever for a start control that is
        // intentionally unavailable mid-wave.
        if (frame.WaveActive.Truth == ObservationTruth.True)
        {
            Transition(SimulacrumPhase.Fighting, frame.Now);
            return Decision(SimulacrumCommand.Fight, $"resume active wave {frame.Wave}");
        }
        if (frame.StartReady.Truth == ObservationTruth.Unknown)
            return Decision(SimulacrumCommand.Wait, "start readiness unknown");
        if (frame.StartReady.Truth != ObservationTruth.True)
            return Decision(SimulacrumCommand.Wait, "waiting for monolith start control");

        Transition(SimulacrumPhase.StartingWave, frame.Now);
        return Decision(SimulacrumCommand.StartWave, $"start wave {Math.Clamp(frame.Wave + 1, 1, _maxWaves)}");
    }

    private SimulacrumDecision TickStarting(SimulacrumFrame frame)
    {
        if (frame.WaveActive.Truth == ObservationTruth.True)
        {
            Transition(SimulacrumPhase.Fighting, frame.Now);
            return Decision(SimulacrumCommand.Fight, $"wave {frame.Wave} active");
        }
        if (Elapsed(frame.Now) > _startTimeout)
            return Fail(frame.Now, $"wave start timed out after {_startTimeout.TotalSeconds:F0}s");
        if (frame.WaveActive.Truth == ObservationTruth.Unknown)
            return Decision(SimulacrumCommand.Wait, "wave-active state unknown");

        // StartingWave is a durable command phase, not a one-tick edge. The adapter may need
        // several ticks to walk into interaction range, click, and wait for memory to confirm
        // active=1. Keep driving that operation until positive evidence arrives or the start
        // timeout fails closed.
        return Decision(SimulacrumCommand.StartWave, "continue monolith interaction until wave-active evidence");
    }

    private SimulacrumDecision TickFighting(SimulacrumFrame frame)
    {
        if (frame.WaveComplete.Truth == ObservationTruth.True)
        {
            Transition(SimulacrumPhase.Looting, frame.Now);
            return Decision(SimulacrumCommand.Loot, $"wave {frame.Wave} complete");
        }
        if (Elapsed(frame.Now) > _waveTimeout)
            return Fail(frame.Now, $"wave {frame.Wave} timed out after {_waveTimeout.TotalMinutes:F1}m");
        if (frame.WaveActive.Truth != ObservationTruth.True || frame.WaveComplete.Truth == ObservationTruth.Unknown)
            return Decision(SimulacrumCommand.Wait, "wave state not positively fightable");
        return Decision(SimulacrumCommand.Fight, $"fight wave {frame.Wave}");
    }

    private SimulacrumDecision TickLooting(SimulacrumFrame frame)
    {
        if (frame.WaveActive.Truth == ObservationTruth.True)
        {
            Transition(SimulacrumPhase.Fighting, frame.Now);
            return Decision(SimulacrumCommand.Fight, $"wave {frame.Wave} active");
        }

        if (frame.InventoryNeedsDeposit)
        {
            Transition(SimulacrumPhase.Depositing, frame.Now);
            return Decision(SimulacrumCommand.Deposit, "inventory deposit threshold reached");
        }
        if (frame.RewardsAvailable.Truth == ObservationTruth.Unknown)
            // The adapter must keep scanning while drops settle. Waiting without running the
            // loot behavior would never discover a label that appears after the first tick.
            return Decision(SimulacrumCommand.Loot, "observe/collect rewards until quiet-window evidence resolves");
        if (frame.RewardsAvailable.Truth == ObservationTruth.True)
            return Decision(SimulacrumCommand.Loot, $"loot wave {frame.Wave} rewards");

        if (frame.Wave >= _maxWaves)
        {
            Phase = SimulacrumPhase.Terminal;
            TerminalReason = $"completed {_maxWaves} waves and rewards are exhausted";
            return Decision(SimulacrumCommand.Leave, TerminalReason);
        }

        Transition(SimulacrumPhase.AwaitingWave, frame.Now);
        return Decision(SimulacrumCommand.Wait, $"wave {frame.Wave} settled; await next start");
    }

    private SimulacrumDecision TickDepositing(SimulacrumFrame frame)
    {
        if (frame.WaveActive.Truth == ObservationTruth.True)
        {
            Transition(SimulacrumPhase.Fighting, frame.Now);
            return Decision(SimulacrumCommand.Fight, $"wave {frame.Wave} active");
        }

        if (frame.InventoryNeedsDeposit)
            return Decision(SimulacrumCommand.Deposit, "deposit still required");
        Transition(SimulacrumPhase.Looting, frame.Now);
        return Decision(SimulacrumCommand.Loot, "deposit complete; resume reward collection");
    }

    private SimulacrumDecision Fail(TimeSpan now, string reason)
    {
        Transition(SimulacrumPhase.Failed, now);
        TerminalReason = reason;
        return Decision(SimulacrumCommand.Stop, reason);
    }

    private void Transition(SimulacrumPhase phase, TimeSpan now)
    {
        Phase = phase;
        _phaseStartedAt = now;
    }

    private TimeSpan Elapsed(TimeSpan now) => now - _phaseStartedAt;
    private SimulacrumDecision Decision(SimulacrumCommand command, string reason)
        => new(Phase, command, reason);
}
