using System.Net;
using System.Net.Sockets;
using System.Numerics;
using CellSimulator.Server.Game;
using CellSimulator.Server.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CellSimulator.Server.Tests;

// Server hardening + the small gameplay fixes that came with it: hostile input, duplicate joins,
// spawning next to something that can eat you, and big cells never shrinking.
public class HardeningTests
{
    private const float Dt = 1f / 30f;

    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(float.PositiveInfinity, 0f)]
    [InlineData(0f, float.NegativeInfinity)]
    public void NonFiniteInput_IsIgnoredAndNeverPoisonsPosition(float x, float y)
    {
        var world = new GameWorld(MapSize.Small);
        var p = world.AddPlayer("p", new IPEndPoint(IPAddress.Loopback, 30000));

        world.SetPlayerInput(p.GroupId, new Vector2(x, y));
        for (int i = 0; i < 10; i++) world.Tick(Dt);

        Assert.True(float.IsFinite(p.Position.X) && float.IsFinite(p.Position.Y));
    }

    [Fact]
    public void OversizedInput_CannotMoveFasterThanTheSpeedLimit()
    {
        var world = new GameWorld(MapSize.Small);
        var p = world.AddPlayer("p", new IPEndPoint(IPAddress.Loopback, 30001));
        p.Position = Vector2.Zero;

        world.SetPlayerInput(p.GroupId, new Vector2(1_000_000f, 0f));
        world.Tick(Dt);

        Assert.True(p.Position.Length() <= GameConfig.Current.MaxSpeed * Dt + 0.01f);
    }

    [Fact]
    public void Spawn_AvoidsBlobsThatCouldEatANewCell()
    {
        var world = new GameWorld(MapSize.Small);
        world.Initialize(); // without this there are no bots and the test proves nothing
        Assert.NotEmpty(world.Bots);
        foreach (var bot in world.Bots) bot.Mass = 20_000f; // scale 80: reach 40, so a sizeable slice of the map is now lethal

        int landedInReach = 0;
        for (int i = 0; i < 500; i++)
        {
            var p = world.AddPlayer("p" + i, new IPEndPoint(IPAddress.Loopback, 31000 + i));
            if (world.Bots.Any(b => Vector2.Distance(b.Position, p.Position) <= b.Scale / 2f)) landedInReach++;
        }

        Assert.Equal(0, landedInReach); // plain random placement lands dozens of 500 here
    }

    [Fact]
    public void MassDecay_ShrinksBigCellsButNeverBelowTheFloorOrSmallOnes()
    {
        Assert.True(Rules.DecayedMass(2000f, 1f) < 2000f);
        Assert.Equal(Rules.MassDecayFloor, Rules.DecayedMass(Rules.MassDecayFloor + 0.0001f, 100f));
        Assert.Equal(100f, Rules.DecayedMass(100f, 1f));
    }

    [Fact]
    public async Task RepeatedJoin_FromSameEndpoint_ReturnsSameSessionInsteadOfAnOrphan()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Server:UdpPort"] = "0" })
            .Build();
        var rooms = new RoomManager(MapSize.Small);
        var udp = new UdpServerService(rooms, NullLogger<UdpServerService>.Instance, config);
        await udp.StartAsync(CancellationToken.None);
        try
        {
            var serverPort = ((IPEndPoint)udp.Socket.Client.LocalEndPoint!).Port;
            using var client = new UdpClient(0);
            var server = new IPEndPoint(IPAddress.Loopback, serverPort);
            byte[] join = { (byte)ClientMsg.Join, 1, (byte)'a', (byte)MapSize.Small };

            var ids = new List<uint>();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await client.SendAsync(join, join.Length, server);
                while (true)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var reply = (await client.ReceiveAsync(cts.Token)).Buffer;
                    if (reply[0] != (byte)ServerMsg.Welcome) continue; // skip FoodFull chunks
                    ids.Add(BitConverter.ToUInt32(reply, 1));
                    break;
                }
            }

            Assert.Equal(ids[0], ids[1]);
            Assert.Single(rooms.Rooms.SelectMany(r => r.World.Players));
        }
        finally
        {
            await udp.StopAsync(CancellationToken.None);
        }
    }
}
