using System.Net;
using System.Numerics;
using CellSimulator.Server.Game;
using CellSimulator.Server.Net;
using Xunit;

namespace CellSimulator.Server.Tests;

// Each viewer's snapshot only carries what its camera can see and always fits one network packet
// (a fragmented UDP snapshot is exactly the bug that made the enlarged map unplayable over the internet).
public class InterestTests
{
    private const float Dt = 1f / 30f;
    private static int _port = 45000;

    private static PlayerEntity Spawn(GameWorld world, float mass, Vector2 at)
    {
        var p = world.AddPlayer("v", new IPEndPoint(IPAddress.Loopback, Interlocked.Increment(ref _port)));
        p.Mass = mass;
        p.Position = at;
        return p;
    }

    private static List<Entity> Everything(GameWorld world)
    {
        var all = new List<Entity>();
        all.AddRange(world.Players);
        all.AddRange(world.Bots);
        all.AddRange(world.Viruses);
        all.AddRange(world.Saws);
        return all;
    }

    private static HashSet<uint> IdsIn(byte[] packet)
    {
        using var r = new BinaryReader(new MemoryStream(packet));
        r.ReadByte(); r.ReadUInt32();
        byte lb = r.ReadByte();
        for (int i = 0; i < lb; i++) { r.ReadBytes(r.ReadByte()); r.ReadSingle(); }
        ushort n = r.ReadUInt16();
        var ids = new HashSet<uint>();
        for (int i = 0; i < n; i++)
        {
            ids.Add(r.ReadUInt32()); r.ReadByte(); r.ReadBytes(8 + 4 + 4 + 4); r.ReadBytes(r.ReadByte());
        }
        return ids;
    }

    [Fact]
    public void ViewerGetsNearbyEntitiesAndOwnPiecesButNotFarOnes()
    {
        var world = new GameWorld(MapSize.Small);
        var viewer = Spawn(world, 400f, Vector2.Zero);
        world.SplitPlayer(viewer.GroupId);
        var ownPieces = world.Players.Where(p => p.GroupId == viewer.GroupId).ToList();
        ownPieces.First(p => p.Id != p.GroupId).Position = new Vector2(120f, 0f); // far apart, still both mine

        var near = Spawn(world, 10f, new Vector2(60f, 20f));
        var far = Spawn(world, 10f, new Vector2(-700f, 700f));

        float zoom = 0f;
        var ids = IdsIn(InterestManager.Encode(1, viewer.GroupId, Everything(world), new List<FoodItem>(), new List<(string, float)>(), ref zoom, Dt));

        Assert.All(ownPieces, p => Assert.Contains(p.Id, ids));
        Assert.Contains(near.Id, ids);
        Assert.DoesNotContain(far.Id, ids);
    }

    [Fact]
    public void FullSizedRoomSnapshotFitsInOnePacket()
    {
        var world = new GameWorld(MapSize.Small);
        world.Initialize();
        var viewer = Spawn(world, 200f, Vector2.Zero);
        for (int i = 0; i < 30; i++) Spawn(world, 50f, new Vector2(i * 8f - 120f, 40f)); // a crowd around the viewer

        var everything = Everything(world);
        Assert.True(Protocol.EncodeSnapshot(1, everything, new List<FoodItem>(), new List<(string, float)>()).Length > InterestManager.MaxPacketBytes,
            "scenario should overflow a packet if sent whole");

        float zoom = 0f;
        var packet = InterestManager.Encode(1, viewer.GroupId, everything, new List<FoodItem>(), new List<(string, float)>(), ref zoom, Dt);

        Assert.True(packet.Length <= InterestManager.MaxPacketBytes, $"{packet.Length} bytes");
        Assert.Contains(viewer.Id, IdsIn(packet));
    }

    [Fact]
    public void WhenTheBudgetIsTightTheFarthestEntitiesGoFirst()
    {
        var world = new GameWorld(MapSize.Small);
        var viewer = Spawn(world, 200f, Vector2.Zero);
        var crowd = Enumerable.Range(0, 80).Select(i => Spawn(world, 30f, new Vector2(10f + i * 2f, 0f))).ToList();

        float zoom = 0f;
        var ids = IdsIn(InterestManager.Encode(1, viewer.GroupId, Everything(world), new List<FoodItem>(), new List<(string, float)>(), ref zoom, Dt));

        Assert.Contains(viewer.Id, ids);
        Assert.Contains(crowd[0].Id, ids);      // nearest survives
        Assert.DoesNotContain(crowd[^1].Id, ids); // farthest was shed to fit
    }

    [Fact]
    public void ViewRadiusShrinksSlowlyAfterASplit_SoTheLaggingCameraStillSeesEverything()
    {
        var world = new GameWorld(MapSize.Small);
        var viewer = Spawn(world, 5000f, Vector2.Zero);      // big: wide camera
        var everything = Everything(world);
        float zoom = 0f;
        InterestManager.Encode(1, viewer.GroupId, everything, new List<FoodItem>(), new List<(string, float)>(), ref zoom, Dt);
        float wide = zoom;

        viewer.Mass = 100f;                                   // suddenly small (e.g. it split)
        InterestManager.Encode(2, viewer.GroupId, everything, new List<FoodItem>(), new List<(string, float)>(), ref zoom, Dt);

        Assert.True(zoom > 0.95f * wide, "zoom must not collapse in a single tick");
        for (int i = 0; i < 300; i++) InterestManager.Encode(3, viewer.GroupId, everything, new List<FoodItem>(), new List<(string, float)>(), ref zoom, Dt);
        Assert.True(zoom < 0.5f * wide, "...but it does settle down eventually");
    }

    [Fact]
    public void EveryClientStillReceivesAllFoodChanges()
    {
        var world = new GameWorld(MapSize.Small);
        var viewer = Spawn(world, 20f, Vector2.Zero);
        var food = Enumerable.Range(0, 40).Select(i => new FoodItem { Id = (uint)(900 + i), Position = new Vector2(700f, -700f) }).ToList();

        float zoom = 0f;
        var packet = InterestManager.Encode(1, viewer.GroupId, Everything(world), food, new List<(string, float)>(), ref zoom, Dt);

        // Far-away pellets must still be in the packet (a skipped update would strand a ghost pellet on the client).
        Assert.True(packet.Length >= 40 * 12);
    }
}
