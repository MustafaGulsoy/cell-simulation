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
            _rooms.Tick(dt, StaleTimeout);
            tick++;

            foreach (var room in _rooms.Rooms)
            {
                Broadcast(room, tick);
            }
        }
    }

    private void Broadcast(Room room, uint tick)
    {
        if (room.Sessions.IsEmpty) return;

        var changedFood = room.World.DrainChangedFood();
        var leaderboard = room.World.GetLeaderboard();
        var packet = Protocol.EncodeSnapshot(tick, room.World.Players, room.World.Bots, room.World.Viruses, room.World.Saws, changedFood, leaderboard);

        foreach (var endPoint in room.Sessions.Keys)
        {
            try
            {
                _udp.Socket.Send(packet, packet.Length, endPoint);
            }
            catch (SocketException)
            {
                // peer likely gone; RoomManager.Tick's stale-player sweep will clean it up
            }
        }
    }
}
