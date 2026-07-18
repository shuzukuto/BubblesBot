using BubblesBot.Core.Game;

namespace BubblesBot.Bot.Systems;

/// <summary>Pure scoring policy for wave-style map exploration.</summary>
public static class FrontierScoring
{
    public readonly record struct Candidate(
        Vector2i Position,
        int PathCost,
        int NewCoverage,
        int NearbyHostiles,
        double DirectionAlignment);

    public readonly record struct Scored(Candidate Candidate, double Score);

    public static double Score(Candidate candidate)
    {
        // Coverage is the primary objective. Dense packs justify a detour, while path cost and
        // reversing direction make repeatedly crossing already-swept ground unattractive.
        var direction = candidate.DirectionAlignment >= 0
            ? candidate.DirectionAlignment * 45.0
            : candidate.DirectionAlignment * 180.0;
        return candidate.NewCoverage * 24.0
             + candidate.NearbyHostiles * 55.0
             + direction
             - candidate.PathCost * 0.65;
    }

    public static Scored? Choose(IReadOnlyList<Candidate> candidates)
    {
        Scored? best = null;
        foreach (var candidate in candidates)
        {
            var scored = new Scored(candidate, Score(candidate));
            if (best is null || scored.Score > best.Value.Score)
                best = scored;
        }
        return best;
    }
}
