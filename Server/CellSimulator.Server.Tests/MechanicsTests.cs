using System.Net;
using System.Numerics;
using CellSimulator.Server.Game;
using CellSimulator.Server.Net;
using Xunit;

namespace CellSimulator.Server.Tests;

// Split / merge / spike / food / speed / map rules from GameConfig. Deterministic wherever the
// rules are (spike piece counts, launch shape, mass conservation) - randomness is only in where
// things spawn, which these tests pin down explicitly.
public class MechanicsTests
{
    private const float Dt = 1f / 30f;
    private static int _port = 40000;
    private static IPEndPoint NextEndPoint() => new(IPAddress.Loopback, Interlocked.Increment(ref _port));

    /// <summary>Runs <paramref name="body"/> under a modified GameConfig and always restores the defaults.</summary>
    private static void WithConfig(Action<GameConfig> tweak, Action body)
    {
        var cfg = new GameConfig();
        tweak(cfg);
        GameConfig.Use(cfg);
        try { body(); }
        finally { GameConfig.Use(new GameConfig()); }
    }

    private static PlayerEntity Spawn(GameWorld world, float mass, Vector2 at)
    {
        var p = world.AddPlayer("p", NextEndPoint());
        p.Mass = mass;
        p.Position = at;
        return p;
    }

    private static List<PlayerEntity> Group(GameWorld world, uint groupId) =>
        world.Players.Where(p => p.GroupId == groupId).ToList();

    // ---- Split: launch ------------------------------------------------------------------------

    [Fact]
    public void Split_LaunchIsSmoothAndTravelsTheConfiguredDistance()
    {
        var world = new GameWorld(MapSize.Small);
        var player = Spawn(world, 400f, Vector2.Zero);
        world.SplitPlayer(player.GroupId);
        var clone = Group(world, player.GroupId).Single(p => p.Id != p.GroupId);

        var cfg = GameConfig.Current;
        float expectedDistance = (cfg.SplitDistance + cfg.SplitDistancePerScale * clone.Scale) * cfg.SplitForce;
        float peak = cfg.SplitSpeed * cfg.SplitForce;

        var start = clone.Position;
        var prev = start;
        float prevSpeed = 0f, maxSpeed = 0f, maxAccelStep = 0f, firstSpeed = -1f;
        while (clone.IsLaunching)
        {
            world.Tick(Dt);
            float speed = Vector2.Distance(prev, clone.Position) / Dt;
            if (firstSpeed < 0f) firstSpeed = speed;
            maxSpeed = MathF.Max(maxSpeed, speed);
            maxAccelStep = MathF.Max(maxAccelStep, MathF.Abs(speed - prevSpeed));
            prevSpeed = speed;
            prev = clone.Position;
        }

        Assert.True(firstSpeed < 0.35f * peak, "launch must ease in, not start at full speed");
        Assert.True(maxSpeed <= peak * 1.05f, $"peak speed {maxSpeed} exceeds SplitSpeed {peak}");
        Assert.True(maxAccelStep < 0.45f * peak, "speed changed too abruptly between ticks");
        Assert.InRange(Vector2.Distance(start, clone.Position), expectedDistance * 0.95f, expectedDistance * 1.05f);
    }

    [Fact]
    public void Split_ForceScalesDistanceAndSpeed()
    {
        float Travel(float force)
        {
            float travelled = 0f;
            WithConfig(c => c.SplitForce = force, () =>
            {
                var world = new GameWorld(MapSize.Small);
                var player = Spawn(world, 400f, Vector2.Zero);
                world.SplitPlayer(player.GroupId);
                var clone = Group(world, player.GroupId).Single(p => p.Id != p.GroupId);
                var start = clone.Position;
                while (clone.IsLaunching) world.Tick(Dt);
                travelled = Vector2.Distance(start, clone.Position);
            });
            return travelled;
        }

        Assert.InRange(Travel(2f) / Travel(1f), 1.9f, 2.1f);
    }

    [Fact]
    public void Split_LaunchedPieceStaysControllableAndHandsOffWithoutASpeedJump()
    {
        var world = new GameWorld(MapSize.Small);
        var player = Spawn(world, 400f, Vector2.Zero);
        world.SetPlayerInput(player.GroupId, new Vector2(0f, 1f));
        world.SplitPlayer(player.GroupId);
        var clone = Group(world, player.GroupId).Single(p => p.Id != p.GroupId);

        var prev = clone.Position;
        float prevSpeed = 0f, worstStep = 0f;
        for (int i = 0; i < 90; i++)
        {
            world.Tick(Dt);
            float speed = Vector2.Distance(prev, clone.Position) / Dt;
            if (i > 0) worstStep = MathF.Max(worstStep, MathF.Abs(speed - prevSpeed));
            prevSpeed = speed;
            prev = clone.Position;
        }

        Assert.True(clone.Position.Y > 5f, "the launched piece ignored the joystick");
        Assert.True(worstStep < 0.45f * GameConfig.Current.SplitSpeed, "speed jumped when the launch ended");
    }

    // ---- Split: merge ---------------------------------------------------------------------------

    [Fact]
    public void Split_MergeTimeIsWithin23To26Seconds()
    {
        var world = new GameWorld(MapSize.Small);
        for (int i = 0; i < 40; i++)
        {
            var player = Spawn(world, 400f, new Vector2(-300f + i * 15f, 0f));
            var before = DateTime.UtcNow;
            world.SplitPlayer(player.GroupId);
            foreach (var piece in Group(world, player.GroupId))
            {
                double wait = (piece.MergeEligibleUtc - before).TotalSeconds;
                Assert.InRange(wait, 22.9, 26.2);
            }
        }
    }

    [Fact]
    public void Merge_GlidesInAndConservesMassEveryTick()
    {
        var world = new GameWorld(MapSize.Small);
        var player = Spawn(world, 300f, Vector2.Zero); // == MassDecayFloor, so decay can't blur the accounting
        world.SplitPlayer(player.GroupId);
        for (int i = 0; i < 60; i++) world.Tick(Dt);
        var pieces = Group(world, player.GroupId);
        Assert.Equal(2, pieces.Count);
        float total = pieces.Sum(p => p.Mass);

        foreach (var p in pieces) p.MergeEligibleUtc = DateTime.MinValue; // "23-26s passed"

        int mergeTicks = 0;
        Vector2 prevAbsorbed = default;
        bool sawAbsorbing = false;
        float worstJump = 0f;
        for (int i = 0; i < 400 && Group(world, player.GroupId).Count > 1; i++)
        {
            world.Tick(Dt);
            var now = Group(world, player.GroupId);
            Assert.InRange(now.Sum(p => p.Mass), total - 0.01f, total + 0.01f);

            var absorbing = now.FirstOrDefault(p => p.AbsorbInto != null);
            if (absorbing != null)
            {
                mergeTicks++;
                if (sawAbsorbing) worstJump = MathF.Max(worstJump, Vector2.Distance(prevAbsorbed, absorbing.Position));
                prevAbsorbed = absorbing.Position;
                sawAbsorbing = true;
            }
        }

        var merged = Group(world, player.GroupId);
        Assert.Single(merged);
        Assert.Equal(player.GroupId, merged[0].Id); // the primary always survives
        Assert.InRange(merged[0].Mass, total - 0.01f, total + 0.01f);
        Assert.True(sawAbsorbing && mergeTicks >= 5, "merge should play out over several ticks, not pop out of existence");
        Assert.True(worstJump < 5f, "absorbed piece teleported instead of gliding");
    }

    [Fact]
    public void SplitPieces_DoNotMergeBeforeTheCooldown()
    {
        var world = new GameWorld(MapSize.Small);
        var player = Spawn(world, 400f, Vector2.Zero);
        world.SplitPlayer(player.GroupId);
        for (int i = 0; i < 300; i++) world.Tick(Dt); // 10s of sim, well under 23s (Tick reads real time, so also instant)

        Assert.Equal(2, Group(world, player.GroupId).Count);
    }

    // ---- Control after split ---------------------------------------------------------------------

    [Fact]
    public void EatenPrimaryPiece_HandsItsIdToASurvivor_SoTheClientKeepsControl()
    {
        var world = new GameWorld(MapSize.Small);
        var a = Spawn(world, 400f, new Vector2(-100f, 0f));
        world.SplitPlayer(a.GroupId);
        for (int i = 0; i < 40; i++) world.Tick(Dt);
        var clone = Group(world, a.GroupId).Single(p => p.Id != p.GroupId);
        clone.Position = new Vector2(300f, 0f);   // far from the predator
        var primary = Group(world, a.GroupId).Single(p => p.Id == p.GroupId);
        primary.Position = new Vector2(-100f, 0f);

        var predator = Spawn(world, 20_000f, primary.Position);
        world.Tick(Dt);

        var left = Group(world, a.GroupId);
        Assert.Single(left);
        Assert.Equal(a.GroupId, left[0].Id);      // the session's id still exists
        Assert.Same(clone, left[0]);              // ...and is now the surviving piece
        Assert.True(predator.Mass > 20_000f);

        var before = left[0].Position;
        world.SetPlayerInput(a.GroupId, new Vector2(1f, 0f));
        for (int i = 0; i < 10; i++) world.Tick(Dt);
        Assert.True(left[0].Position.X > before.X + 1f, "input no longer reaches the surviving piece");
    }

    [Fact]
    public void Snapshot_ListsWhichPiecesBelongToWhichPlayer()
    {
        var world = new GameWorld(MapSize.Small);
        var a = Spawn(world, 400f, Vector2.Zero);
        var solo = Spawn(world, 10f, new Vector2(100f, 100f));
        world.SplitPlayer(a.GroupId);

        var packet = Protocol.EncodeSnapshot(1, world.Players, world.Bots, world.Viruses, world.Saws, new List<FoodItem>(), new List<(string, float)>());
        int pieces = Group(world, a.GroupId).Count;
        int tailStart = packet.Length - 2 - 8 * pieces;

        Assert.Equal(pieces, BitConverter.ToUInt16(packet, tailStart));
        Assert.Equal(a.GroupId, BitConverter.ToUInt32(packet, tailStart + 2 + 4)); // first pair's groupId
        Assert.DoesNotContain(solo.Id, Enumerable.Range(0, pieces).Select(i => BitConverter.ToUInt32(packet, tailStart + 2 + 8 * i)));
    }

    // ---- Spikes ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(170f, 2)]
    [InlineData(399f, 2)]
    [InlineData(400f, 3)]
    [InlineData(1000f, 4)]
    [InlineData(2400f, 8)]
    [InlineData(1_000_000f, 8)] // capped by SpikySplitCount
    public void SpikePieceCount_IsADeterministicFunctionOfMass(float mass, int expected)
    {
        Assert.Equal(expected, Rules.SpikePieceCount(mass));
        Assert.Equal(expected, Rules.SpikePieceCount(mass));
    }

    private static (GameWorld World, PlayerEntity Player, SawEntity Saw) PopScenario(float mass)
    {
        var world = new GameWorld(MapSize.Small);
        world.Initialize();
        var saw = world.Saws.First();
        saw.Position = new Vector2(200f, 200f);
        foreach (var v in world.Viruses) v.Position = new Vector2(-600f, -600f);
        foreach (var other in world.Saws.Skip(1)) other.Position = new Vector2(-600f, 600f);
        foreach (var bot in world.Bots) bot.Position = new Vector2(600f, -600f);
        foreach (var f in world.AllFood()) f.Position = new Vector2(-750f, 750f); // no ordinary food near the scene: masses stay exact
        var player = Spawn(world, mass, saw.Position + new Vector2(-3f, 0f));
        return (world, player, saw);
    }

    [Theory]
    [InlineData(1000f)]
    [InlineData(2500f)]
    public void SpikePop_SplitsIntoTheFormulaCountWithEqualMassAndConservesMass(float mass)
    {
        var (world, player, _) = PopScenario(mass);
        int expected = Rules.SpikePieceCount(mass);
        int pellets = Math.Min(GameConfig.Current.SpikyFoodCount, (int)(mass * 0.2f));

        world.Tick(Dt);
        var pieces = Group(world, player.GroupId);

        Assert.Equal(expected, pieces.Count);
        // Equal split of what's left after paying for the pellets (tiny slack: passive mass decay this tick).
        Assert.All(pieces, p => Assert.InRange(p.Mass, (mass - pellets) / expected - 0.1f, (mass - pellets) / expected + 0.001f));
        Assert.Equal(pieces.Count, pieces.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void SpikePop_TwoIdenticalRunsGiveIdenticalResults()
    {
        (int Count, float Mass) Run()
        {
            var (world, player, _) = PopScenario(1500f);
            world.Tick(Dt);
            var pieces = Group(world, player.GroupId);
            return (pieces.Count, MathF.Round(pieces[0].Mass, 1));
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void SpikePop_ThrowsTheConfiguredNumberOfPelletsWithinTheConfiguredDistance()
    {
        foreach (var (count, distance) in new[] { (8, 40f), (3, 15f), (0, 40f) })
        {
            WithConfig(c => { c.SpikyFoodCount = count; c.SpikyFoodLaunchDistance = distance; }, () =>
            {
                var (world, player, saw) = PopScenario(2000f);
                int foodBefore = world.AllFood().Count;
                var origin = player.Position;

                world.Tick(Dt);
                var flying = world.AllFood().Where(f => f.Velocity != Vector2.Zero).ToList();
                Assert.Equal(count, flying.Count);
                Assert.All(flying, f => Assert.True(f.EjectDirection == Vector2.Zero, "spike food must not feed saws"));

                float farthest = 0f;
                for (int i = 0; i < 400; i++)
                {
                    world.Tick(Dt);
                    foreach (var f in flying.Where(f => f.Velocity != Vector2.Zero)) // still airborne (not eaten + respawned elsewhere)
                    {
                        float d = Vector2.Distance(f.Position, origin);
                        Assert.True(d <= distance * 1.05f + 15f, $"pellet flew {d} > max {distance}");
                        farthest = MathF.Max(farthest, d);
                    }
                }

                if (count > 0) Assert.True(farthest >= distance * 0.3f, "pellets barely travelled");
                Assert.Equal(foodBefore, world.AllFood().Count); // recycled, never added
            });
        }
    }

    [Fact]
    public void Eject_RecyclesFoodInsteadOfGrowingTheWorld()
    {
        var world = new GameWorld(MapSize.Small);
        world.Initialize();
        var player = Spawn(world, 5000f, Vector2.Zero);
        world.SetPlayerInput(player.GroupId, new Vector2(1f, 0f));
        int before = world.AllFood().Count;

        for (int i = 0; i < 20; i++)
        {
            world.EjectMass(player.GroupId);
            Thread.Sleep(Rules.EjectCooldown + TimeSpan.FromMilliseconds(10));
            world.Tick(Dt);
        }

        Assert.Equal(before, world.AllFood().Count);
    }

    // ---- Map -----------------------------------------------------------------------------------------

    [Fact]
    public void Map_SizeComesFromOneConfigAndNothingEverLeavesIt()
    {
        WithConfig(c => { c.MapWidth = 600f; c.MapHeight = 400f; }, () =>
        {
            var world = new GameWorld(MapSize.Small);
            world.Initialize();
            Assert.NotEmpty(world.AllFood());
            Assert.Equal(300f, world.HalfWidth);
            Assert.Equal(200f, world.HalfHeight);
            Assert.Equal(600f, new GameWorld(MapSize.Medium).HalfWidth); // 2x the configured base

            // Spawns, food and bots are all inside.
            Assert.All(world.AllFood(), f => Assert.True(Math.Abs(f.Position.X) <= 300f && Math.Abs(f.Position.Y) <= 200f));
            Assert.All(world.Bots, b => Assert.True(Math.Abs(b.Position.X) <= 300f && Math.Abs(b.Position.Y) <= 200f));
            for (int i = 0; i < 200; i++)
            {
                var p = world.AddPlayer("s", NextEndPoint());
                Assert.True(Math.Abs(p.Position.X) <= 300f && Math.Abs(p.Position.Y) <= 200f);
            }

            // A blob driven hard into each wall/corner stays fully inside it (no half-outside, no stuck-out).
            var player = Spawn(world, 100f, Vector2.Zero);
            foreach (var dir in new[] { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1), new Vector2(1, 1), new Vector2(-1, -1) })
            {
                world.SetPlayerInput(player.GroupId, dir);
                for (int i = 0; i < 700; i++)
                {
                    world.Tick(Dt);
                    if (!world.Players.Contains(player)) break; // eaten by something on the way: nothing left to check
                    float r = player.Scale / 2f;
                    Assert.True(Math.Abs(player.Position.X) <= 300f - r + 0.01f && Math.Abs(player.Position.Y) <= 200f - r + 0.01f,
                        $"dir {dir} tick {i}: pos {player.Position} scale {player.Scale} mass {player.Mass} launching={player.IsLaunching} pieces={world.Players.Count(x => x.GroupId == player.GroupId)}");
                }
            }
        });
    }

    [Fact]
    public void Map_DefaultIsBiggerThanTheOld900AndScalesTheEntityCounts()
    {
        var small = new GameWorld(MapSize.Small);
        Assert.True(small.HalfWidth * 2 > 900f);
        Assert.Equal(small.HalfWidth, small.HalfHeight);
        Assert.True(small.FoodCount > 5300 && small.BotCount > 15);
        Assert.Equal(small.HalfWidth * 2, new GameWorld(MapSize.Medium).HalfWidth);

        var welcome = Protocol.EncodeWelcome(7, 800, 450);
        Assert.Equal(1 + 4 + 4 + 4, welcome.Length);
        Assert.Equal(800, BitConverter.ToInt32(welcome, 5));   // an old client reads only this
        Assert.Equal(450, BitConverter.ToInt32(welcome, 9));
    }

    [Fact]
    public void Config_NonsenseValuesAreRepairedNotObeyed()
    {
        var bad = new GameConfig { MinSpeed = 50f, MaxSpeed = 10f, MergeTimeMin = 30f, MergeTimeMax = 5f, SpikySplitCount = 1, MapWidth = -5f, SplitForce = 0f };
        bad.Normalize();

        Assert.True(bad.MinSpeed <= bad.MaxSpeed);
        Assert.True(bad.MergeTimeMax >= bad.MergeTimeMin);
        Assert.True(bad.SpikySplitCount >= 2);
        Assert.True(bad.MapWidth >= 200f);
        Assert.True(bad.SplitForce > 0f);
    }
}
