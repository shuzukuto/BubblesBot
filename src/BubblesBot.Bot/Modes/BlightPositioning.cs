using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

public readonly record struct BlightDefendGoal(Vector2i Position, string Reason);

/// <summary>Pure defend-position policy so live behavior and replay/tests use identical choices.</summary>
public static class BlightPositioning
{
    public static BlightDefendGoal? Choose(
        Vector2i pump,
        Vector2i player,
        float defendRadius,
        IEnumerable<EntityCache.Entry> entities)
    {
        var radius = Math.Max(12f, defendRadius);

        EntityCache.Entry? nearestHazard = null;
        var nearestHazardD2 = 24f * 24f;
        foreach (var entity in entities)
        {
            if (!entity.IsHazard || entity.IsStale) continue;
            var d2 = DistanceSquared(player, entity.GridPosition);
            if (d2 < nearestHazardD2)
            {
                nearestHazardD2 = d2;
                nearestHazard = entity;
            }
        }

        if (nearestHazard is not null)
        {
            var goal = PointFromPump(pump, nearestHazard.GridPosition, -radius * 0.55f, player);
            if (DistanceSquared(player, goal) > 6f * 6f)
                return new BlightDefendGoal(goal, $"avoid-hazard:{nearestHazard.Id}");
        }

        if (DistanceSquared(player, pump) > radius * radius)
            return new BlightDefendGoal(pump, "outside-defend-radius");

        EntityCache.Entry? dangerous = null;
        var bestRank = 1; // only rare/unique targets justify repositioning away from the pump
        var bestPumpD2 = float.PositiveInfinity;
        var maxThreatD2 = radius * 2f * radius * 2f;
        foreach (var entity in entities)
        {
            if (!TargetEligibility.IsEligible(entity)) continue;
            var rank = Threat.RarityRank(entity);
            if (rank < 2) continue;
            var d2 = DistanceSquared(pump, entity.GridPosition);
            if (d2 > maxThreatD2) continue;
            if (rank > bestRank || rank == bestRank && d2 < bestPumpD2)
            {
                dangerous = entity;
                bestRank = rank;
                bestPumpD2 = d2;
            }
        }

        if (dangerous is not null)
        {
            // Meet the dangerous lane partway while staying well inside the pump leash.
            var goal = PointFromPump(pump, dangerous.GridPosition, radius * 0.35f, player);
            if (DistanceSquared(player, goal) > 8f * 8f)
                return new BlightDefendGoal(goal, $"intercept-threat:{dangerous.Id}:rank={bestRank}");
        }

        return null;
    }

    private static Vector2i PointFromPump(Vector2i pump, Vector2i source, float distance, Vector2i fallback)
    {
        var dx = (float)(source.X - pump.X);
        var dy = (float)(source.Y - pump.Y);
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.001f)
        {
            dx = fallback.X - pump.X;
            dy = fallback.Y - pump.Y;
            length = MathF.Sqrt(dx * dx + dy * dy);
        }
        if (length < 0.001f) { dx = 1f; dy = 0f; length = 1f; }
        return new Vector2i
        {
            X = pump.X + (int)MathF.Round(dx / length * distance),
            Y = pump.Y + (int)MathF.Round(dy / length * distance),
        };
    }

    private static float DistanceSquared(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return dx * dx + dy * dy;
    }
}
