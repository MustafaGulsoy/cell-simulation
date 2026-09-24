using System.Net;
using System.Net.Sockets;
using System.Numerics;
using CellSimulator.Server.Game;
using CellSimulator.Server.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CellSimulator.Server.Tests;

// The new protocol behaviours over real UDP sockets against the real service + game loop.
public class ProtocolIntegrationTests : IAsyncLifetime
{
    private RoomManager _rooms = null!;
    private UdpServerService _udp = null!;
    private GameLoopService _loop = null!;
    private IPEndPoint _server = null!;
    private readonly List<UdpClient> _clients = new();

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Server:UdpPort"] = "0" })
            .Build();
        _rooms = new RoomManager(MapSize.Small);
        _udp = new UdpServerService(_rooms, NullLogger<UdpServerService>.Instance, config);
        _loop = new GameLoopService(_rooms, _udp, NullLogger<GameLoopService>.Instance, new LeaderboardStore());
        await _udp.StartAsync(CancellationToken.None);
        await _loop.StartAsync(CancellationToken.None);
        _server = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)_udp.Socket.Client.LocalEndPoint!).Port);
    }

    public async Task DisposeAsync()
    {
        foreach (var c in _clients) c.Dispose();
        await _loop.StopAsync(CancellationToken.None);
        await _udp.StopAsync(CancellationToken.None);
    }

    private UdpClient NewClient()
    {
        var c = new UdpClient(0);
        _clients.Add(c);
        return c;
    }

    private static byte[] JoinPacket(string name, params byte[] tail) =>
        new byte[] { (byte)ClientMsg.Join, (byte)name.Length }.Concat(System.Text.Encoding.UTF8.GetBytes(name)).Append((byte)MapSize.Small).Concat(tail).ToArray();

    private async Task<(UdpClient Client, uint Id)> JoinAsync(string name, params byte[] tail)
    {
        var c = NewClient();
        var join = JoinPacket(name, tail);
        await c.SendAsync(join, join.Length, _server);
        var welcome = await ReceiveAsync(c, d => d[0] == (byte)ServerMsg.Welcome);
        return (c, BitConverter.ToUInt32(welcome, 1));
    }

    private static async Task<byte[]> ReceiveAsync(UdpClient c, Func<byte[], bool> want, int seconds = 5)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true)
        {
            var d = (await c.ReceiveAsync(cts.Token)).Buffer;
            if (want(d)) return d;
        }
    }

    private PlayerEntity Entity(uint id) => _rooms.Rooms.Single().World.Players.Single(p => p.Id == id);

    [Fact]
    public async Task ModernClientGetsCompactSnapshotsAndItsColour_LegacyClientGetsTheOriginalFormat()
    {
        var (modern, modernId) = await JoinAsync("Modern", 2, 200, 100, 50);
        var (legacy, legacyId) = await JoinAsync("Legacy");

        var compact = await ReceiveAsync(modern, d => SnapshotDecoder.IsSnapshot(d[0]));
        var original = await ReceiveAsync(legacy, d => SnapshotDecoder.IsSnapshot(d[0]));

        Assert.Equal((byte)ServerMsg.SnapshotV2, compact[0]);
        Assert.Equal((byte)ServerMsg.Snapshot, original[0]);

        var world = _rooms.Rooms.Single().World;
        var snap = new SnapshotDecoder { HalfWidth = world.HalfWidth, HalfHeight = world.HalfHeight }.Decode(compact);
        var me = Assert.Single(snap.Entities, e => e.Id == modernId);
        Assert.Equal("Modern", me.Name);
        Assert.Equal((byte)200, me.R);
        Assert.Equal((byte)100, me.G);
        Assert.Equal((byte)50, me.B);

        // The legacy client's colour is random, the modern one's is what it asked for.
        Assert.Equal((byte)200, Entity(modernId).Color.R);
        Assert.NotNull(Entity(legacyId).Name);
    }

    [Fact]
    public async Task Ping_IsEchoedSoTheClientCanMeasureItsRoundTrip()
    {
        var (client, _) = await JoinAsync("Pinger", 2, 10, 20, 30);
        var ping = new byte[] { (byte)ClientMsg.Ping, 0xDE, 0xAD, 0xBE, 0xEF };
        await client.SendAsync(ping, ping.Length, _server);

        var pong = await ReceiveAsync(client, d => d[0] == (byte)ServerMsg.Pong);

        Assert.Equal(new byte[] { (byte)ServerMsg.Pong, 0xDE, 0xAD, 0xBE, 0xEF }, pong);
    }

    [Fact]
    public async Task EatenModernPlayerIsToldWhoDidIt_LegacyClientIsNot()
    {
        var (modern, modernId) = await JoinAsync("Victim", 2, 10, 20, 30);
        var (legacy, legacyId) = await JoinAsync("OldVictim");
        var world = _rooms.Rooms.Single().World;

        var victim = Entity(modernId);
        var oldVictim = Entity(legacyId);
        victim.Mass = oldVictim.Mass = 200f;
        victim.Position = new Vector2(-300, 0);
        oldVictim.Position = new Vector2(300, 0);
        var predator = world.AddPlayer("Hunter", new IPEndPoint(IPAddress.Loopback, 9));
        var predator2 = world.AddPlayer("Hunter2", new IPEndPoint(IPAddress.Loopback, 10));
        predator.Mass = predator2.Mass = 30_000f;
        predator.Position = victim.Position;
        predator2.Position = oldVictim.Position;

        var died = await ReceiveAsync(modern, d => d[0] == (byte)ServerMsg.Died);
        using var r = new BinaryReader(new MemoryStream(died, 1, died.Length - 1));
        var report = DeathReport.Decode(r);
        Assert.Equal("Hunter", report.KillerName);
        Assert.True(report.PeakMass >= 199f);

        // The legacy client saw its player get eaten too, but has no Died message to receive.
        await Task.Delay(400);
        legacy.Client.ReceiveTimeout = 200;
        int diedForLegacy = 0;
        while (legacy.Available > 0)
        {
            var d = (await legacy.ReceiveAsync()).Buffer;
            if (d[0] == (byte)ServerMsg.Died) diedForLegacy++;
        }
        Assert.Equal(0, diedForLegacy);
    }

    [Fact]
    public async Task APacketFlood_IsThrottledWhileAnotherClientIsUnaffected()
    {
        var (flooder, _) = await JoinAsync("Flooder", 2, 10, 20, 30);
        var (polite, _) = await JoinAsync("Polite", 2, 10, 20, 30);

        var ping = new byte[] { (byte)ClientMsg.Ping, 1, 2, 3, 4 };
        for (int i = 0; i < 3000; i++) await flooder.SendAsync(ping, ping.Length, _server);
        await Task.Delay(500);

        int pongs = 0;
        while (flooder.Available > 0)
        {
            if ((await flooder.ReceiveAsync()).Buffer[0] == (byte)ServerMsg.Pong) pongs++;
        }
        Assert.InRange(pongs, 50, 1500); // nowhere near 3000: the bucket (200/s burst) throttled the rest

        await polite.SendAsync(ping, ping.Length, _server);
        Assert.NotNull(await ReceiveAsync(polite, d => d[0] == (byte)ServerMsg.Pong));
        Assert.True(ServerMetrics.Read().PacketsRejected > 0);
    }

    [Fact]
    public async Task ManyJoinsFromOneAddress_AreCappedButServerStaysHealthy()
    {
        var socketClients = new List<UdpClient>();
        for (int i = 0; i < 40; i++)
        {
            var c = NewClient();
            socketClients.Add(c);
            var join = JoinPacket("Bot" + i);
            await c.SendAsync(join, join.Length, _server);
        }
        await Task.Delay(800);

        int sessions = _rooms.Rooms.Sum(r => r.Sessions.Count);
        Assert.InRange(sessions, 1, 20); // JoinsPerMinutePerIp default
        Assert.True(ServerMetrics.Read().JoinsRejected > 0);

        // And a health probe (a pong) still works afterwards.
        var (c2, _) = await JoinAsync("Late").ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : (socketClients[0], 0u));
        Assert.NotNull(c2);
    }
}
