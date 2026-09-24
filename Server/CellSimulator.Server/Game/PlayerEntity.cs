using System.Net;
using System.Numerics;

namespace CellSimulator.Server.Game;

public sealed class PlayerEntity : Entity
{
    public IPEndPoint EndPoint = null!;
    public Vector2 InputDirection;
    public DateTime LastSeenUtc = DateTime.UtcNow;

    // Split lifecycle: GroupId is the original (pre-split) entity Id, shared by every piece a
    // player currently controls - it never changes, so it's what SetPlayerInput/leaderboard/merge
    // key off of instead of the per-piece Id. A piece may not re-merge with a sibling until
    // MergeEligibleUtc passes (prevents instant re-merge right after splitting apart).
    public uint GroupId;
    public DateTime MergeEligibleUtc = DateTime.MinValue;

    // Merge animation (GameWorld.ResolveMerges/AdvanceAbsorptions): while AbsorbInto is set this
    // piece is gliding into that sibling and handing its mass over; it takes no part in anything
    // else (eating, collisions, steering) until it's gone.
    public PlayerEntity? AbsorbInto;
    public Vector2 AbsorbStart;
    public float AbsorbElapsed;
    public float AbsorbMassTotal;
    public float AbsorbMassMoved;

    public PlayerEntity()
    {
        Type = EntityType.Player;
    }
}
