using System.Net.Sockets;
using CellSimulator.Server.Game;
using CellSimulator.Server.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CellSimulator.Server;

/// <summary>Drives every active room's fixed-tick simulation and broadcasts each room's snapshot
/// only to that room's sessions.</summary>
public sealed class GameLoopService : BackgroundService
{
    private const int TickRateHz = 30;
    private static readonly TimeSpan StaleTimeout = TimeSpan.FromSeconds(10);

    private readonly RoomManager _rooms;
    private readonly UdpServerService _udp;
    private readonly ILogger<GameLoopService> _logger;

    public GameLoopService(RoomManager rooms, UdpServerService udp, ILogger<GameLoopService> logger)
    {
        _rooms = rooms;
        _udp = udp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const float dt = 1f / TickRateHz;
        var period = TimeSpan.FromSeconds(dt);
        uint tick = 0;

        using var timer = new PeriodicTimer(period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // A bug in one tick must cost one frame, not the whole process (an unhandled exception
            // in a BackgroundService stops the host, dropping every room).
            try
            {
                _rooms.Tick(dt, StaleTimeout);
                tick++;

                foreach (var room in _rooms.Rooms)
                {
                    Broadcast(room, tick, dt);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Game loop iteration failed");
            }
        }
    }

    private void Broadcast(Room room, uint tick, float dt)
    {
        if (room.Sessions.IsEmpty) return;

        var changedFood = room.World.DrainChangedFood();
        var leaderboard = room.World.GetLeaderboard();

        var everything = new List<Entity>();
        everything.AddRange(room.World.Players);
        everything.AddRange(room.World.Bots);
        everything.AddRange(room.World.Viruses);
        everything.AddRange(room.World.Saws);

        // Each viewer gets its own snapshot (only what its camera can see, within one network
        // packet) - see InterestManager for why the whole room can't be sent to everyone anymore.
        foreach (var (endPoint, sessionId) in room.Sessions)
        {
            try
            {
                float zoom = room.ViewZoom.GetValueOrDefault(endPoint);
                var packet = InterestManager.Encode(tick, sessionId, everything, changedFood, leaderboard, ref zoom, dt);
                room.ViewZoom[endPoint] = zoom;
                _udp.Socket.Send(packet, packet.Length, endPoint);
            }
            catch (SocketException)
            {
                // peer likely gone; RoomManager.Tick's stale-player sweep will clean it up
            }
        }
    }
}
