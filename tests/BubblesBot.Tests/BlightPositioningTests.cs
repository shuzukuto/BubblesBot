using BubblesBot.Bot.Modes;
using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class BlightPositioningTests
{
    [Fact]
    public void Hazard_near_player_moves_to_opposite_side_of_pump()
    {
        var hazard = Entry(10, 10, 0);
        hazard.Disposition = EntityDisposition.Hazard;

        var goal = BlightPositioning.Choose(Pos(0, 0), Pos(5, 0), 40, [hazard]);

        Assert.NotNull(goal);
        Assert.StartsWith("avoid-hazard", goal.Value.Reason);
        Assert.True(goal.Value.Position.X < 0);
        Assert.InRange(Distance(Pos(0, 0), goal.Value.Position), 20, 24);
    }

    [Fact]
    public void Outside_leash_returns_to_pump()
    {
        var goal = BlightPositioning.Choose(Pos(0, 0), Pos(50, 0), 40, []);

        Assert.Equal(Pos(0, 0), goal?.Position);
        Assert.Equal("outside-defend-radius", goal?.Reason);
    }

    [Fact]
    public void Rare_threat_is_intercepted_inside_leash()
    {
        var rare = Entry(20, 70, 0);
        rare.Rarity = EntityListReader.EntityRarity.Rare;

        var goal = BlightPositioning.Choose(Pos(0, 0), Pos(0, 0), 40, [rare]);

        Assert.NotNull(goal);
        Assert.StartsWith("intercept-threat", goal.Value.Reason);
        Assert.InRange(goal.Value.Position.X, 13, 15);
    }

    private static EntityCache.Entry Entry(uint id, int x, int y) => new()
    {
        Id = id,
        Kind = EntityListReader.EntityKind.Monster,
        GridPosition = Pos(x, y),
        Disposition = EntityDisposition.Combatant,
        HpCurrent = 100,
        HpMax = 100,
        LifeReadable = BooleanObservation.Known(true, "Life", 1, ObservationConfidence.Validated),
        Targetability = BooleanObservation.Known(true, "Targetable", 1, ObservationConfidence.Validated),
        AlliedReaction = BooleanObservation.Known(false, "Allied", 1, ObservationConfidence.Validated),
        Dormancy = BooleanObservation.Known(false, "Dormant", 1, ObservationConfidence.Validated),
    };

    private static Vector2i Pos(int x, int y) => new() { X = x, Y = y };
    private static double Distance(Vector2i a, Vector2i b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
