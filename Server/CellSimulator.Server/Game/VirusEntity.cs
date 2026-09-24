namespace CellSimulator.Server.Game;

/// <summary>The big spike: only cells bigger than Rules.VirusScale trigger it. When popped it
/// relocates (GameWorld.ResolveHazardCollisions); fed enough, it splits off a new virus.</summary>
public sealed class VirusEntity : SpikeEntity
{
    public VirusEntity()
    {
        Type = EntityType.Virus;
    }
}
