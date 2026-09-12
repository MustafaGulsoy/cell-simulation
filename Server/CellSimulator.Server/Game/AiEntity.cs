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

    public AiEntity()
    {
        Type = EntityType.Ai;
    }
}
