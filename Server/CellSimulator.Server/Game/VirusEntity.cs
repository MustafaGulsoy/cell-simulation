namespace CellSimulator.Server.Game;

/// <summary>Static hazard: a player cell strictly bigger than Rules.VirusScale that touches one
/// gets forced-split (GameWorld.PopVirusOn); smaller cells just pass through.</summary>
public sealed class VirusEntity : Entity
{
    public VirusEntity()
    {
        Type = EntityType.Virus;
    }
}
