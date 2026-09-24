namespace CellSimulator.Server.Game;

/// <summary>The smaller, more frequent spike: a much lower trigger size than the virus.</summary>
public sealed class SawEntity : SpikeEntity
{
    public SawEntity()
    {
        Type = EntityType.Saw;
    }
}
