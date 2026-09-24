using System.Collections.Concurrent;
using System.Net;

namespace CellSimulator.Server.Game;

/// <summary>One independent match: a GameWorld plus which UDP endpoints belong to it. Only
/// created on demand by RoomManager when a player actually joins - never spun up eagerly with
/// bots and nobody watching.</summary>
public sealed class Room
{
    private static int _nextRoomId = 1;

    public int Id { get; } = _nextRoomId++;
    public GameWorld World { get; }
    public ConcurrentDictionary<IPEndPoint, uint> Sessions { get; } = new();

    /// <summary>Everything the server remembers about one connection besides which entity it controls.</summary>
    public ConcurrentDictionary<IPEndPoint, SessionState> State { get; } = new();

    public Room(MapSize mapSize)
    {
        World = new GameWorld(mapSize);
        World.Initialize();
    }

    public bool IsFull => World.DistinctPlayerCount >= World.PlayerCapacity;
    public MapSize MapSize => World.Size;
}

/// <summary>Per-connection bookkeeping used by the network layer.</summary>
public sealed class SessionState
{
    /// <summary>Protocol version the client announced in Join (0 = predates versioning).</summary>
    public byte ClientVersion;

    /// <summary>Smoothed camera zoom, used by Net/InterestManager to decide how far around a player to send.</summary>
    public float ViewZoom;

    /// <summary>Last tick each entity's name/colour was sent to this client (compact snapshots only
    /// resend those every so often - see SnapshotV2).</summary>
    public Dictionary<uint, uint> LastFullInfoTick { get; } = new();

    /// <summary>Tick of the last leaderboard sent (compact snapshots send it at a lower rate).</summary>
    public uint LastLeaderboardTick;
}
