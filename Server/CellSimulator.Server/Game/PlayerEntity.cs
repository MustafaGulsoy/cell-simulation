using System.Net;
using System.Numerics;

namespace CellSimulator.Server.Game;

public sealed class PlayerEntity : Entity
{
    public IPEndPoint EndPoint = null!;
    public Vector2 InputDirection;
    public DateTime LastSeenUtc = DateTime.UtcNow;

    public PlayerEntity()
    {
        Type = EntityType.Player;
    }
}
