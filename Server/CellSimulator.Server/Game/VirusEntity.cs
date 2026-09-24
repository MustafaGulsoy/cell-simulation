namespace CellSimulator.Server.Game;

/// <summary>Static hazard: a cell strictly bigger than Rules.VirusScale that touches one gets
/// forced-split (GameWorld.ResolveHazardCollisions); smaller cells just pass through.</summary>
public sealed class VirusEntity : Entity
{
    public VirusEntity()
    {
        Type = EntityType.Virus;
    }
}
