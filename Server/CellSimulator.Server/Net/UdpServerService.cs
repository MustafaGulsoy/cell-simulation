using System.Net;
using System.Net.Sockets;
using CellSimulator.Server.Game;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CellSimulator.Server.Net;

/// <summary>Listens for Join/Input packets, routes them through RoomManager. GameLoopService uses
/// the shared UdpClient to broadcast each room's snapshot to that room's sessions.</summary>
public sealed class UdpServerService : BackgroundService
{
    private readonly RoomManager _rooms;
    private readonly ILogger<UdpServerService> _logger;
    private readonly int _port;

    public UdpClient Socket { get; }

    public UdpServerService(RoomManager rooms, ILogger<UdpServerService> logger, IConfiguration config)
    {
        _rooms = rooms;
        _logger = logger;
        _port = config.GetValue("Server:UdpPort", 7778);
        Socket = new UdpClient(_port);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("UDP server listening on port {Port}", _port);
        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await Socket.ReceiveAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            HandlePacket(result.Buffer, result.RemoteEndPoint);
        }
    }

    private void HandlePacket(byte[] data, IPEndPoint from)
    {
        if (!Protocol.TryDecodeClientMsg(data, out var type, out var join, out var input)) return;

        switch (type)
        {
            case ClientMsg.Join:
                var room = _rooms.JoinOrCreateRoom(from);
                var player = room.World.AddPlayer(join.Username, from);
                room.Sessions[from] = player.Id;
                var welcome = Protocol.EncodeWelcome(player.Id, room.World.HalfMapSize);
                Socket.Send(welcome, welcome.Length, from);

                const int chunkSize = 100;
                foreach (var chunk in room.World.AllFood().Chunk(chunkSize))
                {
                    var packet = Protocol.EncodeFoodChunk(chunk);
                    Socket.Send(packet, packet.Length, from);
                }

                _logger.LogInformation("Player {Name} joined room {RoomId} as {Id} from {EndPoint}",
                    player.Name, room.Id, player.Id, from);
                break;

            case ClientMsg.Input:
                if (_rooms.TryGetRoom(from, out var existingRoom) && existingRoom.Sessions.TryGetValue(from, out var id))
                {
                    existingRoom.World.SetPlayerInput(id, input.Direction);
                }
                break;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Socket.Dispose();
    }
}
