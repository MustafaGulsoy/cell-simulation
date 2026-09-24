using System.Collections.Concurrent;
using System.Net;
using System.Threading.Tasks;

namespace CellSimulator.Server.Game;

/// <summary>Assigns joining players to a room instead of the server running one eternal world
/// (with bots) whether or not anyone's playing. A join goes to the first room with space, or a
/// fresh room is created for it; a room with no players left is torn down immediately.</summary>
public sealed class RoomManager
{
    private readonly MapSize _defaultMapSize;
    private readonly object _gate = new();
    private readonly List<Room> _rooms = new();
    private readonly ConcurrentDictionary<IPEndPoint, Room> _roomByEndpoint = new();

    public RoomManager(MapSize defaultMapSize)
    {
        _defaultMapSize = defaultMapSize;
    }

    public IReadOnlyList<Room> Rooms
    {
        get { lock (_gate) { return _rooms.ToArray(); } }
    }

    /// <summary>requestedSize comes from the client's Join packet (its map-size dropdown
    /// selection) - only rooms of that same size are matched/created, so "Small" players never
    /// land in a "Huge" room and vice versa.</summary>
    public Room JoinOrCreateRoom(IPEndPoint endPoint, MapSize? requestedSize = null)
    {
        var size = requestedSize ?? _defaultMapSize;
        lock (_gate)
        {
            var room = _rooms.FirstOrDefault(r => !r.IsFull && r.MapSize == size);
            if (room == null)
            {
                room = new Room(size);
                _rooms.Add(room);
            }
            _roomByEndpoint[endPoint] = room;
            return room;
        }
    }

    /// <summary>How many live sessions come from this source address (for the per-IP cap).</summary>
    public int SessionsFromAddress(IPAddress address)
    {
        int count = 0;
        foreach (var endPoint in _roomByEndpoint.Keys)
        {
            if (endPoint.Address.Equals(address)) count++;
        }
        return count;
    }

    public bool TryGetRoom(IPEndPoint endPoint, out Room room) => _roomByEndpoint.TryGetValue(endPoint, out room!);

    /// <summary>Ticks every active room, drops stale sessions, and closes any room that's now empty.</summary>
    public void Tick(float dt, TimeSpan staleTimeout)
    {
        Room[] snapshot;
        lock (_gate)
        {
            snapshot = _rooms.ToArray();
        }

        // Rooms don't share any state (each owns its own GameWorld, internally lock-protected),
        // so independent rooms can tick across the box's cores instead of queuing behind each
        // other on one thread - this is what actually lets room count scale with CPU count.
        Parallel.ForEach(snapshot, room => room.World.Tick(dt));

        lock (_gate)
        {
            for (int i = _rooms.Count - 1; i >= 0; i--)
            {
                var room = _rooms[i];

                foreach (var staleId in room.World.RemoveStalePlayers(staleTimeout))
                {
                    var stale = room.Sessions.FirstOrDefault(kv => kv.Value == staleId);
                    if (!stale.Equals(default(KeyValuePair<IPEndPoint, uint>)))
                    {
                        room.Sessions.TryRemove(stale.Key, out _);
                        room.State.TryRemove(stale.Key, out _);
                        _roomByEndpoint.TryRemove(stale.Key, out _);
                    }
                }

                if (room.World.Players.Count == 0)
                {
                    _rooms.RemoveAt(i);
                }
            }
        }
    }
}
