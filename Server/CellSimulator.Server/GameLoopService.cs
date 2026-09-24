using System.Diagnostics;
using System.Net.Sockets;
using CellSimulator.Server.Game;
using CellSimulator.Server.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CellSimulator.Server;

/// <summary>Drives every active room's fixed-tick simulation and sends each session its own
/// snapshot (see InterestManager), plus the one-off end-of-life messages.</summary>
public sealed class GameLoopService : BackgroundService
{
    private const int TickRateHz = 30;
    private static readonly TimeSpan StaleTimeout = TimeSpan.FromSeconds(10);

    // An iteration over budget is worth a log line, but not one per tick when the box is struggling.
    private const double SlowTickWarnMs = 40;
    private static readonly TimeSpan SlowTickLogEvery = TimeSpan.FromSeconds(10);

    private readonly RoomManager _rooms;
    private readonly UdpServerService _udp;
    private readonly ILogger<GameLoopService> _logger;
    private readonly LeaderboardStore? _leaderboard;
    private DateTime _lastSlowTickLog = DateTime.MinValue;

    public GameLoopService(RoomManager rooms, UdpServerService udp, ILogger<GameLoopService> logger, LeaderboardStore? leaderboard = null)
    {
        _rooms = rooms;
        _udp = udp;
        _logger = logger;
        _leaderboard = leaderboard;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const float dt = 1f / TickRateHz;
        var period = TimeSpan.FromSeconds(dt);
        uint tick = 0;

        using var timer = new PeriodicTimer(period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var clock = Stopwatch.StartNew();

            // A bug in one tick must cost one frame, not the whole process (an unhandled exception
            // in a BackgroundService stops the host, dropping every room).
            try
            {
                _rooms.Tick(dt, StaleTimeout);
                tick++;

                foreach (var room in _rooms.Rooms)
                {
                    Broadcast(room, tick, dt);
                    HandleDeaths(room);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Game loop iteration failed");
            }

            double ms = clock.Elapsed.TotalMilliseconds;
            ServerMetrics.Tick(ms);
            if (ms > SlowTickWarnMs && DateTime.UtcNow - _lastSlowTickLog > SlowTickLogEvery)
            {
                _lastSlowTickLog = DateTime.UtcNow;
                _logger.LogWarning("Slow game-loop iteration: {Ms:F1} ms (budget {Budget:F0} ms)", ms, 1000.0 / TickRateHz);
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
        everything.AddRange(room.World.Powerups);

        // Each viewer gets its own snapshot (only what its camera can see, within one network
        // packet) - see InterestManager for why the whole room can't be sent to everyone anymore.
        foreach (var (endPoint, sessionId) in room.Sessions)
        {
            try
            {
                var state = room.State.GetOrAdd(endPoint, _ => new SessionState());
                var packet = InterestManager.EncodeFor(tick, sessionId, state, everything, changedFood, leaderboard,
                    room.World.HalfWidth, room.World.HalfHeight, dt);
                _udp.Socket.Send(packet, packet.Length, endPoint);
                ServerMetrics.PacketOut(packet.Length);
                ServerMetrics.Snapshot(packet.Length);
            }
            catch (SocketException)
            {
                // peer likely gone; RoomManager.Tick's stale-player sweep will clean it up
            }
        }
    }

    /// <summary>Finished lives: tell the player how it ended (clients that understand the message),
    /// and put the run on the persistent leaderboards.</summary>
    private void HandleDeaths(Room room)
    {
        foreach (var death in room.World.DrainDeaths())
        {
            ServerMetrics.Death();
            _leaderboard?.Record(death.PlayerName, death.PeakMass, death.SurvivedSeconds);

            if (death.Reason != DeathReason.Eaten) continue;
            if (!room.State.TryGetValue(death.EndPoint, out var state) || state.ClientVersion < 2) continue;

            try
            {
                var packet = Protocol.EncodeDied(death);
                _udp.Socket.Send(packet, packet.Length, death.EndPoint);
                ServerMetrics.PacketOut(packet.Length);
            }
            catch (SocketException)
            {
                // gone already
            }
        }
    }
}
