namespace CellSimulator.Server.Game;

/// <summary>Shared base of the two static hazards (virus, saw). Both pop cells bigger than them
/// (GameWorld.ResolveHazardCollisions) and both can be FED: every FeedThreshold ejected pellets
/// that reach one, it launches a brand new spike of its own kind in the direction the pellet came
/// from (GameWorld.ResolveHazardFeeding) - agar.io's virus-feeding mechanic.</summary>
public abstract class SpikeEntity : Entity
{
    public int FeedCount;
    public int FeedThreshold = 2;
}
