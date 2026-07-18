using BubblesBot.Bot.Modes;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class SimulacrumControllerTests
{
    [Theory]
    [InlineData(0, 2, true)]
    [InlineData(1, 2, true)]
    [InlineData(2, 2, false)]
    [InlineData(3, 2, false)]
    [InlineData(-1, 2, false)]
    [InlineData(0, 0, false)]
    public void Portal_refresh_budget_has_a_hard_attempt_boundary(
        int attempts, int maximum, bool expected)
    {
        Assert.Equal(expected,
            SimulacrumController.CanRefreshPortal(attempts, maximum));
    }

    [Theory]
    [InlineData(3, 3, 4)]
    [InlineData(7, 0, 7)]
    [InlineData(1, 0, 1)]
    public void Delayed_wave_counter_is_correlated_on_active_edge(
        int observedWave, int lastKnownWave, int expected)
    {
        Assert.Equal(expected,
            SimulacrumController.CorrelateStartedWave(observedWave, lastKnownWave));
    }

    [Fact]
    public void Critical_unknowns_fail_closed()
    {
        var controller = new SimulacrumController();
        var decision = controller.Tick(Frame(TimeSpan.Zero));

        Assert.Equal(SimulacrumPhase.AwaitingWave, decision.Phase);
        Assert.Equal(SimulacrumCommand.Wait, decision.Command);
    }

    [Fact]
    public void Positive_evidence_advances_start_fight_and_loot()
    {
        var controller = new SimulacrumController();

        Assert.Equal(SimulacrumCommand.StartWave,
            controller.Tick(Frame(TimeSpan.Zero, start: True("start"))).Command);
        Assert.Equal(SimulacrumCommand.Fight,
            controller.Tick(Frame(TimeSpan.FromSeconds(1), wave: 1, active: True("active"))).Command);
        Assert.Equal(SimulacrumCommand.Loot,
            controller.Tick(Frame(TimeSpan.FromSeconds(5), wave: 1, active: True("active"), complete: True("done"))).Command);
    }

    [Fact]
    public void Reattach_during_active_wave_resumes_combat_without_clicking_start()
    {
        var controller = new SimulacrumController();
        var decision = controller.Tick(Frame(TimeSpan.Zero, wave: 7, active: True("active")));

        Assert.Equal(SimulacrumPhase.Fighting, decision.Phase);
        Assert.Equal(SimulacrumCommand.Fight, decision.Command);
    }

    [Fact]
    public void Starting_wave_persists_interaction_until_active_evidence_arrives()
    {
        var controller = new SimulacrumController();
        controller.Tick(Frame(TimeSpan.Zero, wave: 1, start: True("start"), active: False("active")));

        var continueStart = controller.Tick(Frame(TimeSpan.FromSeconds(1), wave: 1,
            start: True("start"), active: False("active")));
        Assert.Equal(SimulacrumPhase.StartingWave, continueStart.Phase);
        Assert.Equal(SimulacrumCommand.StartWave, continueStart.Command);

        var fight = controller.Tick(Frame(TimeSpan.FromSeconds(2), wave: 2, active: True("active")));
        Assert.Equal(SimulacrumPhase.Fighting, fight.Phase);
        Assert.Equal(SimulacrumCommand.Fight, fight.Command);
    }

    [Fact]
    public void Final_wave_requires_rewards_exhausted_before_leaving()
    {
        var controller = FightingWave(15);
        controller.Tick(Frame(TimeSpan.FromSeconds(2), wave: 15, active: True("active"), complete: True("done")));

        Assert.Equal(SimulacrumCommand.Loot,
            controller.Tick(Frame(TimeSpan.FromSeconds(3), wave: 15, rewards: True("rewards"))).Command);
        var terminal = controller.Tick(Frame(TimeSpan.FromSeconds(4), wave: 15, rewards: False("rewards")));
        Assert.Equal(SimulacrumPhase.Terminal, terminal.Phase);
        Assert.Equal(SimulacrumCommand.Leave, terminal.Command);
    }

    [Fact]
    public void Reattach_between_waves_runs_reward_collection_before_next_start()
    {
        var controller = new SimulacrumController();
        controller.ResumeBetweenWaves(TimeSpan.Zero);

        var settling = controller.Tick(Frame(TimeSpan.FromSeconds(1), wave: 3));
        Assert.Equal(SimulacrumPhase.Looting, settling.Phase);
        Assert.Equal(SimulacrumCommand.Loot, settling.Command);

        var settled = controller.Tick(Frame(TimeSpan.FromSeconds(6), wave: 3,
            rewards: False("quiet")));
        Assert.Equal(SimulacrumPhase.AwaitingWave, settled.Phase);
        Assert.Equal(SimulacrumCommand.Wait, settled.Command);
    }

    [Fact]
    public void Deposit_phase_remains_latched_while_adapter_reports_incomplete()
    {
        var controller = FightingWave(3);
        controller.Tick(Frame(TimeSpan.FromSeconds(2), wave: 3,
            active: False("active"), complete: True("done")));

        var startDeposit = controller.Tick(Frame(TimeSpan.FromSeconds(3), wave: 3,
            rewards: False("quiet"), deposit: true));
        Assert.Equal(SimulacrumPhase.Depositing, startDeposit.Phase);
        Assert.Equal(SimulacrumCommand.Deposit, startDeposit.Command);

        var stillDeposit = controller.Tick(Frame(TimeSpan.FromSeconds(4), wave: 3,
            deposit: true));
        Assert.Equal(SimulacrumPhase.Depositing, stillDeposit.Phase);
        Assert.Equal(SimulacrumCommand.Deposit, stillDeposit.Command);

        var completed = controller.Tick(Frame(TimeSpan.FromSeconds(5), wave: 3,
            deposit: false));
        Assert.Equal(SimulacrumPhase.Looting, completed.Phase);
    }

    [Fact]
    public void Death_budget_and_wave_timeout_stop_explicitly()
    {
        var deaths = new SimulacrumController(maxDeaths: 1);
        Assert.Equal(SimulacrumCommand.Stop,
            deaths.Tick(Frame(TimeSpan.Zero, deaths: 2)).Command);

        var timeout = FightingWave(1, waveTimeout: TimeSpan.FromSeconds(3));
        var stopped = timeout.Tick(Frame(TimeSpan.FromSeconds(5), wave: 1,
            active: True("active"), complete: False("done")));
        Assert.Equal(SimulacrumPhase.Failed, stopped.Phase);
        Assert.Equal(SimulacrumCommand.Stop, stopped.Command);
    }

    [Fact]
    public void Completed_fight_sweep_refreshes_stall_timeout()
    {
        var controller = FightingWave(13, waveTimeout: TimeSpan.FromSeconds(3));
        controller.ReportFightProgress(TimeSpan.FromSeconds(3));

        var stillFighting = controller.Tick(Frame(TimeSpan.FromSeconds(5), wave: 13,
            active: True("active"), complete: False("done")));
        Assert.Equal(SimulacrumPhase.Fighting, stillFighting.Phase);
        Assert.Equal(SimulacrumCommand.Fight, stillFighting.Command);

        var stalled = controller.Tick(Frame(TimeSpan.FromSeconds(7), wave: 13,
            active: True("active"), complete: False("done")));
        Assert.Equal(SimulacrumPhase.Failed, stalled.Phase);
    }

    private static SimulacrumController FightingWave(int wave, TimeSpan? waveTimeout = null)
    {
        var controller = new SimulacrumController(waveTimeout: waveTimeout);
        controller.Tick(Frame(TimeSpan.Zero, start: True("start")));
        controller.Tick(Frame(TimeSpan.FromSeconds(1), wave: wave, active: True("active")));
        return controller;
    }

    private static SimulacrumFrame Frame(
        TimeSpan now,
        int wave = 0,
        int deaths = 0,
        BooleanObservation? start = null,
        BooleanObservation? active = null,
        BooleanObservation? complete = null,
        BooleanObservation? rewards = null,
        bool deposit = false)
        => new(now, wave, deaths,
            start ?? Unknown("start"), active ?? Unknown("active"),
            complete ?? Unknown("complete"), rewards ?? Unknown("rewards"), deposit);

    private static BooleanObservation True(string source)
        => BooleanObservation.Known(true, source, 1, ObservationConfidence.Validated);
    private static BooleanObservation False(string source)
        => BooleanObservation.Known(false, source, 1, ObservationConfidence.Validated);
    private static BooleanObservation Unknown(string source)
        => BooleanObservation.Unknown(source, 1, ObservationReadStatus.ReadFailed, ObservationConfidence.Experimental);
}
