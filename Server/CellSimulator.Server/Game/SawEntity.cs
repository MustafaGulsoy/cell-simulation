namespace CellSimulator.Server.Game;

/// <summary>Static hazard: any player or bot touching it takes periodic mass damage
/// (GameWorld.ResolveSawCollisions), unlike the virus which only affects players.</summary>
public sealed class SawEntity : Entity
{
    public SawEntity()
    {
        Type = EntityType.Saw;
    }
}
