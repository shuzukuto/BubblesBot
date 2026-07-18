using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;

namespace BubblesBot.Tests;

public sealed class FrontierScoringTests
{
    [Fact]
    public void NewCoverageBeatsACloserBacktrack()
    {
        var candidates = new[]
        {
            Candidate(20, coverage: 3, pack: 0, alignment: -1),
            Candidate(70, coverage: 18, pack: 0, alignment: 0.7),
        };

        var chosen = FrontierScoring.Choose(candidates);

        Assert.NotNull(chosen);
        Assert.Equal(70, chosen.Value.Candidate.Position.X);
    }

    [Fact]
    public void DensePackCanJustifyAReachableDetour()
    {
        var candidates = new[]
        {
            Candidate(40, coverage: 10, pack: 0, alignment: 0.5),
            Candidate(65, coverage: 8, pack: 8, alignment: 0.1),
        };

        var chosen = FrontierScoring.Choose(candidates);

        Assert.Equal(65, chosen!.Value.Candidate.Position.X);
    }

    [Fact]
    public void ReversalIsPenalizedWhenOtherEvidenceIsEqual()
    {
        var candidates = new[]
        {
            Candidate(30, coverage: 10, pack: 2, alignment: -0.8),
            Candidate(31, coverage: 10, pack: 2, alignment: 0.8),
        };

        var chosen = FrontierScoring.Choose(candidates);

        Assert.Equal(31, chosen!.Value.Candidate.Position.X);
    }

    private static FrontierScoring.Candidate Candidate(
        int x, int coverage, int pack, double alignment)
        => new(new Vector2i { X = x, Y = 0 }, x, coverage, pack, alignment);
}
