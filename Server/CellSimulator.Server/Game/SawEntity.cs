namespace CellSimulator.Server.Game;

/// <summary>Static hazard: any player or bot bigger than it that touches it gets forced-split
/// (GameWorld.ResolveHazardCollisions - the same deterministic rule as the virus, just a much
/// smaller trigger size). Feeding it ejected mass (GameWorld.ResolveSawFeeding) periodically
/// launches a new saw in the direction that feed came from.</summary>
public sealed class SawEntity : Entity
{
    public int FeedCount;
    public int FeedThreshold = 2;

    public SawEntity()
    {
        Type = EntityType.Saw;
    }
}
