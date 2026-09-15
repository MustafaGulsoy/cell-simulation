namespace CellSimulator.Server.Game;

/// <summary>Static hazard: any player or bot bigger than it that touches it gets forced-split
/// into a few unevenly-sized pieces (GameWorld.ResolveSawCollisions), unlike the virus (equal
/// split, players only). Feeding it ejected mass (GameWorld.ResolveSawFeeding) periodically
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
