using System.Numerics;

namespace CellSimulator.Server.Game;

/// <summary>Ported from AIMovement/AIRange - simple distance-based roam/chase/flee FSM, no physics.</summary>
public static class AiBrain
{
    private const float SenseRange = 40f;
    private const float RoamArriveDist = 5f;

    // How much farther than SenseRange a locked-on threat/target may drift before we drop it.
    // Without this, a candidate hovering right at the SenseRange/CanEat boundary flips the bot's
    // MoveDirection every single tick (chase <-> roam, back and forth) since Think() re-picks
    // the "closest" candidate from scratch each tick with no stickiness.
    private const float StickyRangeMultiplier = 1.5f;

    public static void Think(AiEntity bot, GameWorld world, Random rng, float dt)
    {
        if (bot.ThreatId.HasValue)
        {
            var threat = world.FindBlob(bot.ThreatId.Value);
            if (threat != null && Rules.CanEat(threat.Scale, bot.Scale) &&
                Vector2.Distance(bot.Position, threat.Position) <= SenseRange * StickyRangeMultiplier)
            {
                bot.State = AiState.Running;
                bot.MoveDirection = SafeDirection(bot.Position - threat.Position);
                return;
            }
            bot.ThreatId = null;
        }

        if (bot.TargetId.HasValue)
        {
            var target = world.FindBlob(bot.TargetId.Value);
            if (target != null && Rules.CanEat(bot.Scale, target.Scale) &&
                Vector2.Distance(bot.Position, target.Position) <= SenseRange * StickyRangeMultiplier)
            {
                bot.State = AiState.Chasing;
                bot.MoveDirection = SafeDirection(target.Position - bot.Position);
                return;
            }
            bot.TargetId = null;
        }

        Entity? closestThreat = null, closestPrey = null;
        float closestThreatDist = float.MaxValue, closestPreyDist = float.MaxValue;

        foreach (var p in world.Players) ConsiderCandidate(bot, p, ref closestThreat, ref closestThreatDist, ref closestPrey, ref closestPreyDist);
        foreach (var a in world.Bots)
        {
            if (a.Id == bot.Id) continue;
            ConsiderCandidate(bot, a, ref closestThreat, ref closestThreatDist, ref closestPrey, ref closestPreyDist);
        }

        if (closestThreat != null)
        {
            bot.State = AiState.Running;
            bot.ThreatId = closestThreat.Id;
            bot.MoveDirection = SafeDirection(bot.Position - closestThreat.Position);
            return;
        }

        if (closestPrey != null)
        {
            bot.State = AiState.Chasing;
            bot.TargetId = closestPrey.Id;
            bot.MoveDirection = SafeDirection(closestPrey.Position - bot.Position);
            return;
        }

        bot.State = AiState.Roaming;
        bot.TargetId = null;
        bot.ThreatId = null;
        if (Vector2.Distance(bot.Position, bot.RoamTarget) < RoamArriveDist)
        {
            float half = world.HalfMapSize;
            bot.RoamTarget = new Vector2(
                (float)(rng.NextDouble() * 2 - 1) * half,
                (float)(rng.NextDouble() * 2 - 1) * half);
        }
        bot.MoveDirection = SafeDirection(bot.RoamTarget - bot.Position);
    }

    private static void ConsiderCandidate(
        AiEntity bot, Entity other,
        ref Entity? closestThreat, ref float closestThreatDist,
        ref Entity? closestPrey, ref float closestPreyDist)
    {
        float dist = Vector2.Distance(bot.Position, other.Position);
        if (dist > SenseRange) return;

        if (Rules.CanEat(other.Scale, bot.Scale))
        {
            if (dist < closestThreatDist) { closestThreatDist = dist; closestThreat = other; }
        }
        else if (Rules.CanEat(bot.Scale, other.Scale))
        {
            if (dist < closestPreyDist) { closestPreyDist = dist; closestPrey = other; }
        }
    }

    private static Vector2 SafeDirection(Vector2 v) => v.LengthSquared() > 0.0001f ? Vector2.Normalize(v) : Vector2.Zero;
}
