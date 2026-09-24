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

            // One bad packet (or a failed send to a vanished peer) must never take the whole
            // listener down - an unhandled exception here stops the host for every room.
            try
            {
                HandlePacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dropped packet from {EndPoint}", result.RemoteEndPoint);
            }
        }
    }

    private void HandlePacket(byte[] data, IPEndPoint from)
    {
        if (!Protocol.TryDecodeClientMsg(data, out var type, out var join, out var input, out var emoji)) return;

        switch (type)
        {
            case ClientMsg.Join:
                // The client retries Join until it sees Welcome (UDP can drop either packet), so a
                // repeat from an endpoint that already has a session just gets Welcome + food
                // again - creating another player here would leave an orphan behind.
                if (_rooms.TryGetRoom(from, out var joinedRoom) && joinedRoom.Sessions.TryGetValue(from, out var joinedId))
                {
                    SendWelcome(joinedRoom, joinedId, from);
                    break;
                }

                var room = _rooms.JoinOrCreateRoom(from, join.MapSize);
                var player = room.World.AddPlayer(join.Username, from);
                room.Sessions[from] = player.Id;
                SendWelcome(room, player.Id, from);

                _logger.LogInformation("Player {Name} joined room {RoomId} as {Id} from {EndPoint}",
                    player.Name, room.Id, player.Id, from);
                break;

            case ClientMsg.Input:
                if (_rooms.TryGetRoom(from, out var existingRoom) && existingRoom.Sessions.TryGetValue(from, out var id))
                {
                    existingRoom.World.SetPlayerInput(id, input.Direction);
                }
                break;

            case ClientMsg.Split:
                if (_rooms.TryGetRoom(from, out var splitRoom) && splitRoom.Sessions.TryGetValue(from, out var splitId))
                {
                    splitRoom.World.SplitPlayer(splitId);
                }
                break;

            case ClientMsg.Eject:
                if (_rooms.TryGetRoom(from, out var ejectRoom) && ejectRoom.Sessions.TryGetValue(from, out var ejectId))
                {
                    ejectRoom.World.EjectMass(ejectId);
                }
                break;

            case ClientMsg.Emoji:
                // Relayed straight to the room's sessions, right here off the tick loop - no kill
                // log, no per-tick batching, just a fire-and-forget broadcast like Welcome/FoodFull.
                // Still cooldown-gated through GameWorld so one client can't flood everyone else's
                // bandwidth by spamming Emoji packets.
                if (_rooms.TryGetRoom(from, out var emojiRoom) && emojiRoom.Sessions.TryGetValue(from, out var emojiEntityId)
                    && emojiRoom.World.TryEmojiCooldown(emojiEntityId))
                {
                    var packet = Protocol.EncodeEmojiEvent(emojiEntityId, emoji.EmojiId);
                    foreach (var endPoint in emojiRoom.Sessions.Keys)
                    {
                        try { Socket.Send(packet, packet.Length, endPoint); } catch (SocketException) { }
                    }
                }
                break;
        }
    }

    private void SendWelcome(Room room, uint playerId, IPEndPoint to)
    {
        var welcome = Protocol.EncodeWelcome(playerId, (int)MathF.Ceiling(room.World.HalfWidth), (int)MathF.Ceiling(room.World.HalfHeight));
        Socket.Send(welcome, welcome.Length, to);

        // A big map means ~100+ food chunks (~120 KB). Fired back-to-back that overruns a client's UDP
        // receive buffer (64 KB by default on Windows) and the overflow is silently lost - and food
        // positions are never re-sent, so those pellets would be missing for the whole session.
        // Pacing them out costs the client well under a second.
        const int chunkSize = 100;
        var chunks = room.World.AllFood().Chunk(chunkSize).Select(c => Protocol.EncodeFoodChunk(c)).ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < chunks.Count; i++)
                {
                    Socket.Send(chunks[i], chunks[i].Length, to);
                    if (i % 6 == 5) await Task.Delay(2);
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // peer gone or server stopping - nothing left to send to
            }
        });
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Socket.Dispose();
    }
}
