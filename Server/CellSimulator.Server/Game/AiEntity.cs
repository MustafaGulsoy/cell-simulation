using System.Numerics;

namespace CellSimulator.Server.Game;

public enum AiState
{
    Roaming,
    Chasing,
    Running,
}

public sealed class AiEntity : Entity
{
    public AiState State = AiState.Roaming;
    public uint? TargetId;
    public uint? ThreatId;
    public Vector2 RoamTarget;
    public Vector2 MoveDirection;

    // Bots have no group/merge system like players do, but virus/saw pops still create several
    // sibling pieces at once - without this they'd have no protection against immediately eating
    // each other (a bigger sibling devouring a smaller one the same tick it was created), and
    // since eaten bots always respawn in place rather than disappearing, that would inject free
    // mass into the world. 0 = not part of any pop batch, normal eating rules apply.
    public uint PopBatchId;

    public AiEntity()
    {
        Type = EntityType.Ai;
    }
}
