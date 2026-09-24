using System.Net;
using System.Net.Sockets;
using System.Numerics;
using CellSimulator.Server.Game;
using CellSimulator.Server.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CellSimulator.Server.Tests;

// Several players at once: a long scripted match checking world invariants every tick, and a real
// UDP session with three clients checking the wire protocol end to end.
public class MultiplayerTests
{
    private const float Dt = 1f / 30f;

    [Fact]
    public void FourPlayersSplittingEjectingAndCollidingForNinetySeconds_NeverBreakTheWorld()
    {
        var world = new GameWorld(MapSize.Small);
        world.Initialize();
        var rng = new Random(1234);

        var groups = new List<uint>();
        for (int i = 0; i < 4; i++)
        {
            var p = world.AddPlayer("P" + i, new IPEndPoint(IPAddress.Loopback, 50000 + i));
            p.Mass = 1500f;
            p.Position = new Vector2(-60f + i * 40f, 0f); // bunched together so they really do collide/eat/block
            groups.Add(p.GroupId);
        }

        var lastPos = new Dictionary<uint, (Vector2 Pos, float Mass)>();
        var launchedLastTick = new HashSet<uint>();
        var lastLaunchTick = new Dictionary<uint, int>();
        int ticks = (int)(90 / Dt);
        for (int tick = 0; tick < ticks; tick++)
        {
            if (tick % 20 == 0)
            {
                foreach (var g in groups)
                {
                    var angle = (float)(rng.NextDouble() * Math.PI * 2);
                    world.SetPlayerInput(g, rng.Next(5) == 0 ? Vector2.Zero : new Vector2(MathF.Cos(angle), MathF.Sin(angle)));
                }
            }
            if (tick % 75 == 0) world.SplitPlayer(groups[tick / 75 % groups.Count]);
            if (tick % 50 == 0) world.EjectMass(groups[(tick / 50 + 1) % groups.Count]);

            world.Tick(Dt);

            var players = world.Players;
            var everyone = players.Cast<Entity>().Concat(world.Bots).ToList();

            Assert.Equal(everyone.Count, everyone.Select(e => e.Id).Distinct().Count()); // ids never collide
            Assert.Equal(world.FoodCount, world.AllFood().Count);                        // thrown food is recycled

            foreach (var e in everyone)
            {
                Assert.True(float.IsFinite(e.Position.X) && float.IsFinite(e.Position.Y) && float.IsFinite(e.Mass), $"{e.Name} went non-finite");
                float r = e.Scale / 2f;
                Assert.True(Math.Abs(e.Position.X) <= world.HalfWidth - r + 0.01f && Math.Abs(e.Position.Y) <= world.HalfHeight - r + 0.01f, $"{e.Name} left the map");

                // No teleports: anything that moved a lot in one tick must be a respawn (mass reset) or a mid-merge glide.
                // (Bots are exempt: a bot eaten at minimum mass respawns with the same mass, so a respawn can't be told from a jump.)
                bool absorbing = e is PlayerEntity { AbsorbInto: not null };
                if (e is PlayerEntity && lastPos.TryGetValue(e.Id, out var before) && !absorbing)
                {
                    bool respawned = e.Mass <= Rules.MassMin + 0.01f && before.Mass > Rules.MassMin * 2f;
                    bool promoted = e is PlayerEntity pe && pe.Id == pe.GroupId; // heir took over the primary Id: same object, but a different one than last tick
                    if (!respawned && !promoted)
                    {
                        Assert.True(Vector2.Distance(before.Pos, e.Position) <= 10f, $"{e.Name} jumped {Vector2.Distance(before.Pos, e.Position):F1} units in one tick");
                    }
                }
            }
            lastPos = everyone.ToDictionary(e => e.Id, e => (e.Position, e.Mass));
            launchedLastTick = players.Where(p => p.IsLaunching).Select(p => p.Id).ToHashSet();

            // Every joined player always still has the entity their session is bound to.
            foreach (var g in groups) Assert.Contains(players, p => p.Id == g);

            // Split siblings that can't merge yet don't sink into each other (tiny slack: capped slide-out speed).
            var now = DateTime.UtcNow;
            foreach (var grp in players.Where(p => p.AbsorbInto == null).GroupBy(p => p.GroupId))
            {
                if (grp.Any(p => p.IsLaunching)) lastLaunchTick[grp.Key] = tick;
                // While pieces are still being thrown through a pile of siblings they can shove others into each
                // other for a few ticks (a 13-piece pile can't compress); once the throw is over and the solver has
                // had half a second, the pile must be clean.
                float allowed = tick - lastLaunchTick.GetValueOrDefault(grp.Key, -1000) > 15 ? 0.5f : 3.5f;
                var ps = grp.Where(p => !p.IsLaunching).ToList();
                for (int a = 0; a < ps.Count; a++)
                    for (int b = a + 1; b < ps.Count; b++)
                    {
                        if (now >= ps[a].MergeEligibleUtc && now >= ps[b].MergeEligibleUtc) continue;
                        float overlap = (ps[a].Scale + ps[b].Scale) / 2f - Vector2.Distance(ps[a].Position, ps[b].Position);
                        Assert.True(overlap < allowed, $"siblings overlap by {overlap:F1} at tick {tick}: A(scale {ps[a].Scale:F1} mass {ps[a].Mass:F0} pos {ps[a].Position} wasLaunching={launchedLastTick.Contains(ps[a].Id)} elig={now >= ps[a].MergeEligibleUtc}) B(scale {ps[b].Scale:F1} mass {ps[b].Mass:F0} pos {ps[b].Position} wasLaunching={launchedLastTick.Contains(ps[b].Id)} elig={now >= ps[b].MergeEligibleUtc}) pieces={ps.Count} half=({world.HalfWidth},{world.HalfHeight})");
                    }
            }
        }
    }

    // ---- real sockets ------------------------------------------------------------------------

    private sealed record Snapshot(uint Tick, List<(uint Id, byte Type, Vector2 Pos, float Mass)> Entities, Dictionary<uint, uint> GroupOf, int BytesBeforeTail, int Length);

    private static Snapshot Decode(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        Assert.Equal((byte)ServerMsg.Snapshot, r.ReadByte());
        uint tick = r.ReadUInt32();

        byte lb = r.ReadByte();
        for (int i = 0; i < lb; i++) { r.ReadBytes(r.ReadByte()); r.ReadSingle(); }

        var entities = new List<(uint, byte, Vector2, float)>();
        ushort n = r.ReadUInt16();
        for (int i = 0; i < n; i++)
        {
            uint id = r.ReadUInt32(); byte type = r.ReadByte();
            var pos = new Vector2(r.ReadSingle(), r.ReadSingle());
            r.ReadSingle(); float mass = r.ReadSingle();
            r.ReadBytes(4); r.ReadBytes(r.ReadByte());
            entities.Add((id, type, pos, mass));
        }

        ushort food = r.ReadUInt16();
        r.ReadBytes(food * 12);

        int beforeTail = (int)ms.Position; // an old client stops reading right here
        var groupOf = new Dictionary<uint, uint>();
        ushort owners = r.ReadUInt16();
        for (int i = 0; i < owners; i++) groupOf[r.ReadUInt32()] = r.ReadUInt32();

        return new Snapshot(tick, entities, groupOf, beforeTail, data.Length);
    }

    private static async Task<byte[]> Receive(UdpClient c, Func<byte[], bool> want, int seconds = 5)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true)
        {
            var d = (await c.ReceiveAsync(cts.Token)).Buffer;
            if (want(d)) return d;
        }
    }

    [Fact]
    public async Task ThreeClientsOverUdp_JoinSeeEachOtherSplitAndKeepControl()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Server:UdpPort"] = "0" })
            .Build();
        var rooms = new RoomManager(MapSize.Small);
        var udp = new UdpServerService(rooms, NullLogger<UdpServerService>.Instance, config);
        var loop = new GameLoopService(rooms, udp, NullLogger<GameLoopService>.Instance);
        await udp.StartAsync(CancellationToken.None);
        await loop.StartAsync(CancellationToken.None);

        var clients = new List<UdpClient>();
        try
        {
            var server = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)udp.Socket.Client.LocalEndPoint!).Port);
            byte[] join(string name) => new byte[] { (byte)ClientMsg.Join, (byte)name.Length }.Concat(System.Text.Encoding.UTF8.GetBytes(name)).Append((byte)MapSize.Small).ToArray();

            var ids = new List<uint>();
            foreach (var name in new[] { "A", "B", "C" })
            {
                var c = new UdpClient(0);
                clients.Add(c);
                await c.SendAsync(join(name), join(name).Length, server);
                var welcome = await Receive(c, d => d[0] == (byte)ServerMsg.Welcome);
                Assert.Equal(13, welcome.Length);
                ids.Add(BitConverter.ToUInt32(welcome, 1));
                Assert.Equal(new GameWorld(MapSize.Small).HalfWidth, BitConverter.ToInt32(welcome, 5), precision: 0);
                Assert.Equal(new GameWorld(MapSize.Small).HalfHeight, BitConverter.ToInt32(welcome, 9), precision: 0);
            }
            Assert.Equal(3, ids.Distinct().Count());
            Assert.Single(rooms.Rooms); // all three share one room

            // Everyone steers; A gets big enough to split and asks for it.
            var room = rooms.Rooms.Single();
            var a = room.World.Players.Single(p => p.Id == ids[0]);
            a.Mass = 600f;
            // Snapshots only carry what's near the viewer now: put the three players in each other's view.
            foreach (var (id, i) in ids.Select((id, i) => (id, i)))
                room.World.Players.Single(p => p.Id == id).Position = new Vector2(i * 15f, 0f);
            for (int i = 0; i < 3; i++)
            {
                var input = new byte[9]; input[0] = (byte)ClientMsg.Input;
                BitConverter.GetBytes(1f).CopyTo(input, 1); BitConverter.GetBytes(0f).CopyTo(input, 5);
                await clients[i].SendAsync(input, input.Length, server);
            }
            await clients[0].SendAsync(new[] { (byte)ClientMsg.Split }, 1, server);

            // Every client sees all three players; A's snapshot lists A's pieces under A's session id.
            foreach (var (client, i) in clients.Select((c, i) => (c, i)))
            {
                var snap = Decode(await Receive(client, d => d[0] == (byte)ServerMsg.Snapshot && Decode(d).GroupOf.Count(kv => kv.Value == ids[0]) >= 2));
                Assert.All(ids, id => Assert.Contains(snap.Entities, e => e.Id == id));
                Assert.All(snap.Entities, e => Assert.True(float.IsFinite(e.Pos.X) && float.IsFinite(e.Pos.Y)));
                Assert.Contains(snap.GroupOf, kv => kv.Key == ids[0] && kv.Value == ids[0]); // A's primary is in its own group
                Assert.True(snap.BytesBeforeTail < snap.Length); // an old client that stops here still parsed a whole snapshot
            }

            // Ticks only ever move forward on the wire.
            uint last = 0;
            for (int i = 0; i < 20; i++)
            {
                var snap = Decode(await Receive(clients[1], d => d[0] == (byte)ServerMsg.Snapshot));
                Assert.True(snap.Tick > last);
                last = snap.Tick;
            }

            // A's input is still honoured with several pieces out (it keeps moving right).
            var startX = room.World.Players.Where(p => p.GroupId == ids[0]).Average(p => p.Position.X);
            await Task.Delay(1000);
            var endX = room.World.Players.Where(p => p.GroupId == ids[0]).Average(p => p.Position.X);
            Assert.True(endX > startX + 3f, $"split player stopped responding (x {startX:F1} -> {endX:F1})");
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
            await loop.StopAsync(CancellationToken.None);
            await udp.StopAsync(CancellationToken.None);
        }
    }
}
