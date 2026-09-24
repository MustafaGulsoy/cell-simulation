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

    /// <summary>Per-session smoothed camera zoom, used by Net/InterestManager to decide how far around a player to send.</summary>
    public ConcurrentDictionary<IPEndPoint, float> ViewZoom { get; } = new();

    public Room(MapSize mapSize)
    {
        World = new GameWorld(mapSize);
        World.Initialize();
    }

    public bool IsFull => World.DistinctPlayerCount >= World.PlayerCapacity;
    public MapSize MapSize => World.Size;
}
