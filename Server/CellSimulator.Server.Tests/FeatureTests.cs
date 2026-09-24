using System.Net;
using System.Numerics;
using CellSimulator.Server.Game;
using CellSimulator.Server.Net;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CellSimulator.Server.Tests;

// Power-ups, deaths + stats, virus feeding, bot AI, persistent leaderboard, abuse limits, metrics,
// and the compact snapshot format checked against the Unity client's own decoder.
public class FeatureTests
{
    private const float Dt = 1f / 30f;
    private static int _port = 50000;
    private static IPEndPoint NextEndPoint() => new(IPAddress.Loopback, Interlocked.Increment(ref _port));

    private static PlayerEntity Spawn(GameWorld world, float mass, Vector2 at, string name = "P")
    {
        var p = world.AddPlayer(name, NextEndPoint());
        p.Mass = mass;
        p.Position = at;
        return p;
    }

    /// <summary>A populated world with everything that could interfere (bots, spikes, pickups, food) parked in corners.</summary>
    private static GameWorld Arena()
    {
        var world = new GameWorld(MapSize.Small);
        world.Initialize();
        foreach (var b in world.Bots) b.Position = new Vector2(700f, -700f);
        foreach (var v in world.Viruses) v.Position = new Vector2(-700f, -700f);
        foreach (var s in world.Saws) s.Position = new Vector2(-700f, 700f);
        foreach (var p in world.Powerups) p.Position = new Vector2(700f, 700f);
        foreach (var f in world.AllFood()) f.Position = new Vector2(-750f, 0f);
        return world;
    }

    // ---- Join protocol -----------------------------------------------------------------------------

    [Fact]
    public void Join_CarriesAnOptionalVersionAndColour_OldClientsStillParse()
    {
        byte[] Join(params byte[] tail) => new byte[] { (byte)ClientMsg.Join, 3, (byte)'a', (byte)'b', (byte)'c', (byte)MapSize.Small }.Concat(tail).ToArray();

        Assert.True(Protocol.TryDecodeClientMsg(Join(), out _, out var legacy, out _, out _));
        Assert.Equal(0, legacy.ClientVersion);
        Assert.Null(legacy.PreferredColor);

        Assert.True(Protocol.TryDecodeClientMsg(Join(2, 10, 20, 30), out _, out var modern, out _, out _));
        Assert.Equal(2, modern.ClientVersion);
        Assert.Equal((byte)10, modern.PreferredColor!.Value.R);
        Assert.Equal("abc", modern.Username);

        Assert.True(Protocol.TryDecodeClientMsg(Join(2), out _, out var noColour, out _, out _)); // version but no colour
        Assert.Null(noColour.PreferredColor);
    }

    [Fact]
    public void ChosenColour_IsSanitisedSoNobodyCanImpersonateASpikeOrDisappear()
    {
        var spike = Rgba.Sanitize(new Rgba { R = 60, G = 220, B = 90, A = 0 });
        Assert.True(spike.G <= 150 || spike.R >= 110 || spike.B >= 130, "spike green must not pass through");
        Assert.Equal(255, spike.A);

        var dark = Rgba.Sanitize(new Rgba { R = 0, G = 0, B = 0, A = 255 });
        var light = Rgba.Sanitize(new Rgba { R = 255, G = 255, B = 255, A = 255 });
        Assert.All(new[] { dark.R, dark.G, dark.B }, c => Assert.InRange(c, (byte)25, (byte)235));
        Assert.All(new[] { light.R, light.G, light.B }, c => Assert.InRange(c, (byte)25, (byte)235));

        var world = new GameWorld(MapSize.Small);
        var p = world.AddPlayer("x", NextEndPoint(), new Rgba { R = 200, G = 100, B = 50, A = 255 });
        Assert.Equal((byte)200, p.Color.R);
        Assert.Equal((byte)100, p.Color.G);
    }

    // ---- Deaths and life stats -----------------------------------------------------------------------

    [Fact]
    public void LastCellEaten_ProducesADeathWithKillerAndStats_ExactlyOnce()
    {
        var world = Arena();
        var victim = Spawn(world, 300f, new Vector2(0, 0), "Victim");
        var foods = world.AllFood().Take(3).ToList();
        foreach (var f in foods) { f.Position = victim.Position; f.EatImmunity = 0; }
        for (int i = 0; i < 3; i++) world.Tick(Dt); // one pellet per tick

        var predator = Spawn(world, 20_000f, victim.Position, "Predator");
        world.Tick(Dt);

        var deaths = world.DrainDeaths();
        var death = Assert.Single(deaths, d => d.PlayerName == "Victim");
        Assert.Equal(DeathReason.Eaten, death.Reason);
        Assert.Equal("Predator", death.KillerName);
        Assert.True(death.FoodEaten >= 3, $"food eaten {death.FoodEaten}");
        Assert.True(death.PeakMass >= 300f);
        Assert.True(death.SurvivedSeconds >= 0f);
        Assert.DoesNotContain(world.DrainDeaths(), d => d.PlayerName == "Victim"); // reported once

        // The predator's own stats counted the meal: when it dies in turn, that shows.
        var bigger = Spawn(world, 20_000f, predator.Position, "Bigger");
        bigger.Mass = 80_000f;
        predator.Mass = 20_000f;
        world.Tick(Dt);
        var predatorDeath = world.DrainDeaths().FirstOrDefault(d => d.PlayerName == "Predator");
        if (predatorDeath != null) Assert.True(predatorDeath.BlobsEaten >= 1);
    }

    [Fact]
    public void SplitPlayerWithSurvivingPieces_HasNotDiedYet()
    {
        var world = Arena();
        var a = Spawn(world, 400f, new Vector2(-100, 0), "A");
        world.SplitPlayer(a.GroupId);
        for (int i = 0; i < 40; i++) world.Tick(Dt);
        world.Players.First(p => p.GroupId == a.GroupId && p.Id != p.GroupId).Position = new Vector2(300, 0);

        Spawn(world, 20_000f, a.Position, "Big"); // eats only the primary
        world.Tick(Dt);

        Assert.DoesNotContain(world.DrainDeaths(), d => d.PlayerName == "A");
    }

    [Fact]
    public void SpikeHits_AreCountedInTheLifeStats()
    {
        var world = Arena();
        var saw = world.Saws.First();
        saw.Position = new Vector2(100, 100);
        var p = Spawn(world, 1000f, saw.Position + new Vector2(-2, 0), "Popped");
        world.Tick(Dt);
        Assert.True(world.Players.Count(x => x.GroupId == p.GroupId) > 1); // it popped

        foreach (var piece in world.Players.Where(x => x.GroupId == p.GroupId).ToList()) piece.Position = new Vector2(-300, 300);
        // Everything of that player is eaten one piece at a time.
        for (int i = 0; i < 12 && world.Players.Any(x => x.GroupId == p.GroupId); i++)
        {
            var target = world.Players.First(x => x.GroupId == p.GroupId);
            var eater = Spawn(world, 30_000f, target.Position, "Eater" + i);
            world.Tick(Dt);
            foreach (var e in world.Players.Where(x => x.Name.StartsWith("Eater")).ToList()) e.Position = new Vector2(700, 700);
        }

        var death = world.DrainDeaths().FirstOrDefault(d => d.PlayerName == "Popped" && d.Reason == DeathReason.Eaten);
        Assert.NotNull(death);
        Assert.Equal(1, death!.SpikesHit);
    }

    [Fact]
    public void DisconnectingPlayer_IsRecordedForTheLeaderboardButNotAsAKill()
    {
        var world = Arena();
        var p = Spawn(world, 500f, Vector2.Zero, "Quitter");
        world.Tick(Dt);
        Thread.Sleep(30);

        world.RemoveStalePlayers(TimeSpan.FromMilliseconds(1));

        var death = Assert.Single(world.DrainDeaths(), d => d.PlayerName == "Quitter");
        Assert.Equal(DeathReason.Disconnected, death.Reason);
        Assert.True(death.PeakMass >= 499f); // (passive decay shaves a hair off 500 before the peak is sampled)
    }

    [Fact]
    public void DiedPacket_RoundTripsThroughTheClientDecoder()
    {
        var death = new DeathInfo(1, "V", NextEndPoint(), DeathReason.Eaten, "Killer<>", 1234.5f, 98.25f, 70000, 3, 2);
        var packet = Protocol.EncodeDied(death);

        Assert.Equal((byte)ServerMsg.Died, packet[0]);
        using var r = new BinaryReader(new MemoryStream(packet, 1, packet.Length - 1));
        var report = DeathReport.Decode(r);
        Assert.Equal("Killer<>", report.KillerName);
        Assert.Equal(1234.5f, report.PeakMass);
        Assert.Equal(98.25f, report.SurvivedSeconds);
        Assert.Equal(65535, report.FoodEaten); // clamped, not wrapped
        Assert.Equal(3, report.BlobsEaten);
        Assert.Equal(2, report.SpikesHit);
    }

    // ---- Power-ups --------------------------------------------------------------------------------------

    [Fact]
    public void CollectingAPowerup_GrantsTheWholeGroupThenTheItemGoesDormant()
    {
        var world = Arena();
        var speed = world.Powerups.First(p => p.Kind == PowerupKind.Speed);
        var player = Spawn(world, 400f, new Vector2(0, 0), "Collector");
        world.SplitPlayer(player.GroupId);
        for (int i = 0; i < 40; i++) world.Tick(Dt);
        var pieces = world.Players.Where(p => p.GroupId == player.GroupId).ToList();
        Assert.True(pieces.Count >= 2);

        int before = world.Powerups.Count;
        speed.Position = pieces[0].Position;
        world.Tick(Dt);

        var now = DateTime.UtcNow;
        Assert.All(pieces, p => Assert.True(p.HasSpeedBoost(now), "every piece of the group gets the effect"));
        Assert.Equal(before - 1, world.Powerups.Count); // dormant: not offered or collectable
        Assert.True(speed.RespawnAtUtc > now.AddSeconds(10));

        // A piece created AFTER pickup inherits it too.
        world.SplitPlayer(player.GroupId);
        Assert.All(world.Players.Where(p => p.GroupId == player.GroupId), p => Assert.True(p.HasSpeedBoost(DateTime.UtcNow)));

        speed.RespawnAtUtc = DateTime.MinValue; // "20s passed"
        Assert.Contains(speed, world.Powerups);
    }

    [Fact]
    public void SpeedBoost_MakesACellFasterForItsDuration()
    {
        float Travel(bool boosted)
        {
            var world = Arena();
            var p = Spawn(world, 100f, Vector2.Zero);
            if (boosted) p.SpeedBoostUntil = DateTime.UtcNow.AddSeconds(30);
            world.SetPlayerInput(p.GroupId, new Vector2(1, 0));
            for (int i = 0; i < 30; i++) world.Tick(Dt);
            return p.Position.X;
        }

        Assert.InRange(Travel(true) / Travel(false), GameConfig.Current.SpeedBoostMultiplier * 0.95f, GameConfig.Current.SpeedBoostMultiplier * 1.05f);
    }

    [Fact]
    public void Shield_MakesACellUneatableAndImmuneToSpikes_ButNotForever()
    {
        var world = Arena();
        var victim = Spawn(world, 100f, new Vector2(0, 0), "Shielded");
        victim.ShieldUntil = DateTime.UtcNow.AddSeconds(30);
        var predator = Spawn(world, 20_000f, victim.Position, "Predator");
        float before = predator.Mass;
        world.Tick(Dt);

        Assert.Contains(victim, world.Players);
        Assert.DoesNotContain(world.DrainDeaths(), d => d.PlayerName == "Shielded");
        Assert.True(predator.Mass <= before + 1.01f, "predator must not have eaten it");

        // Spikes ignore it too.
        var saw = world.Saws.First();
        saw.Position = new Vector2(200, 200);
        var big = Spawn(world, 1000f, saw.Position, "BigShielded");
        big.ShieldUntil = DateTime.UtcNow.AddSeconds(30);
        world.Tick(Dt);
        Assert.Single(world.Players, p => p.GroupId == big.GroupId);

        // Once it expires the cell is prey again.
        victim.ShieldUntil = DateTime.MinValue;
        predator.Position = victim.Position;
        world.Tick(Dt);
        Assert.Contains(world.DrainDeaths(), d => d.PlayerName == "Shielded");
    }

    [Fact]
    public void Magnet_ReachesFartherAndSwallowsSeveralPelletsPerTick()
    {
        int Eaten(bool magnet)
        {
            var world = Arena();
            var p = Spawn(world, 100f, Vector2.Zero);
            if (magnet) p.MagnetUntil = DateTime.UtcNow.AddSeconds(30);
            var foods = world.AllFood().Take(6).ToList();
            for (int i = 0; i < foods.Count; i++)
            {
                foods[i].Position = new Vector2(10f + i * 0.2f, 0); // outside normal reach (~6.7), inside magnet reach (~14.7)
                foods[i].EatImmunity = 0;
            }
            float before = p.Mass;
            world.Tick(Dt);
            return (int)MathF.Round(p.Mass - before);
        }

        Assert.Equal(0, Eaten(magnet: false));
        Assert.Equal(4, Eaten(magnet: true));
    }

    [Fact]
    public void Powerups_AreOnlySentToClientsThatUnderstandThem()
    {
        var world = Arena();
        var viewer = Spawn(world, 100f, Vector2.Zero, "Viewer");
        var pickup = world.Powerups.First();
        pickup.Position = new Vector2(20, 0);
        var everything = new List<Entity>();
        everything.AddRange(world.Players); everything.AddRange(world.Bots); everything.AddRange(world.Viruses); everything.AddRange(world.Saws); everything.AddRange(world.Powerups);

        var decoder = new SnapshotDecoder { HalfWidth = world.HalfWidth, HalfHeight = world.HalfHeight };
        var legacy = decoder.Decode(InterestManager.EncodeFor(1, viewer.GroupId, new SessionState { ClientVersion = 0 }, everything, new List<FoodItem>(), new List<(string, float)>(), world.HalfWidth, world.HalfHeight, Dt));
        var modern = new SnapshotDecoder { HalfWidth = world.HalfWidth, HalfHeight = world.HalfHeight }
            .Decode(InterestManager.EncodeFor(1, viewer.GroupId, new SessionState { ClientVersion = 2 }, everything, new List<FoodItem>(), new List<(string, float)>(), world.HalfWidth, world.HalfHeight, Dt));

        Assert.False(legacy.IsCompact);
        Assert.DoesNotContain(legacy.Entities, e => e.Type == (byte)EntityType.Powerup); // an old client would draw it as a bot
        Assert.True(modern.IsCompact);
        var seen = Assert.Single(modern.Entities, e => e.Type == (byte)EntityType.Powerup);
        Assert.Equal(pickup.Name, seen.Name);
    }

    // ---- Compact snapshot vs the client decoder -------------------------------------------------------------

    private static (GameWorld World, PlayerEntity Viewer, List<Entity> Everything) CrowdedScene()
    {
        var world = new GameWorld(MapSize.Small);
        world.Initialize();
        var viewer = Spawn(world, 400f, new Vector2(10.37f, -20.11f), "Viewer");
        world.SplitPlayer(viewer.GroupId);
        for (int i = 0; i < 12; i++) world.Bots.ElementAt(i).Position = new Vector2(-40f + i * 7.3f, 5f + i);
        viewer.SpeedBoostUntil = DateTime.UtcNow.AddSeconds(5);
        var everything = new List<Entity>();
        everything.AddRange(world.Players); everything.AddRange(world.Bots); everything.AddRange(world.Viruses); everything.AddRange(world.Saws); everything.AddRange(world.Powerups);
        return (world, viewer, everything);
    }

    [Fact]
    public void CompactSnapshot_DecodesToTheSameWorldWithinQuantisationError()
    {
        var (world, viewer, everything) = CrowdedScene();
        var session = new SessionState { ClientVersion = 2 };
        var food = world.AllFood().Take(50).ToList();
        var packet = InterestManager.EncodeFor(7, viewer.GroupId, session, everything, food, world.GetLeaderboard(), world.HalfWidth, world.HalfHeight, Dt);

        var snap = new SnapshotDecoder { HalfWidth = world.HalfWidth, HalfHeight = world.HalfHeight }.Decode(packet);

        Assert.True(snap.IsCompact);
        Assert.Equal(7u, snap.Tick);
        Assert.NotNull(snap.Leaderboard); // first packet carries the leaderboard
        Assert.NotEmpty(snap.Entities);
        foreach (var e in snap.Entities)
        {
            var truth = everything.Single(x => x.Id == e.Id);
            Assert.InRange(Math.Abs(e.X - truth.Position.X), 0f, 0.06f);
            Assert.InRange(Math.Abs(e.Y - truth.Position.Y), 0f, 0.06f);
            Assert.InRange(Math.Abs(e.Scale - truth.Scale), 0f, 0.006f);
            Assert.Equal(truth.Mass, e.Mass);
            Assert.Equal((byte)truth.Type, e.Type);
            Assert.Equal(truth.Name, e.Name);
            Assert.Equal(truth.Color.R, e.R);
            Assert.True(e.HasInfo);
        }
        Assert.Contains(snap.Entities, e => e.Id == viewer.Id && (e.Effects & 1) != 0); // speed glow flag
        Assert.NotEmpty(snap.GroupOf);                                                   // the split player's pieces are listed

        Assert.Equal(food.Count, snap.Food.Count);
        for (int i = 0; i < food.Count; i++)
        {
            Assert.Equal(food[i].Id, snap.Food[i].Id);
            Assert.InRange(Math.Abs(snap.Food[i].X - food[i].Position.X), 0f, 0.05f);
            Assert.InRange(Math.Abs(snap.Food[i].Y - food[i].Position.Y), 0f, 0.05f);
        }
    }

    [Fact]
    public void CompactSnapshot_IsMuchSmallerAndAfterTheFirstPacketDropsNamesAndColours()
    {
        var (world, viewer, everything) = CrowdedScene();
        var session = new SessionState { ClientVersion = 2 };
        var lb = world.GetLeaderboard();

        var oldFormat = InterestManager.Encode(10, viewer.GroupId, everything, new List<FoodItem>(), lb, ref session.ViewZoom, Dt);
        var first = InterestManager.EncodeFor(10, viewer.GroupId, session, everything, new List<FoodItem>(), lb, world.HalfWidth, world.HalfHeight, Dt);
        var second = InterestManager.EncodeFor(11, viewer.GroupId, session, everything, new List<FoodItem>(), lb, world.HalfWidth, world.HalfHeight, Dt);

        Assert.True(first.Length < oldFormat.Length, $"first compact {first.Length} vs old {oldFormat.Length}");
        Assert.True(second.Length < first.Length * 0.6, $"steady-state compact {second.Length} vs first {first.Length}");
        Assert.True(second.Length < oldFormat.Length * 0.65, $"steady-state {second.Length} should be well under the old format {oldFormat.Length}");

        var decoder = new SnapshotDecoder { HalfWidth = world.HalfWidth, HalfHeight = world.HalfHeight };
        var firstSnap = decoder.Decode(first);
        var secondSnap = decoder.Decode(second);
        Assert.Null(secondSnap.Leaderboard); // 2 Hz, not every tick
        Assert.Equal(firstSnap.Entities.Count, secondSnap.Entities.Count);
        Assert.All(secondSnap.Entities, e =>
        {
            Assert.True(e.HasInfo);                                   // remembered by the decoder
            Assert.Equal(everything.Single(x => x.Id == e.Id).Name, e.Name);
        });
    }

    [Fact]
    public void CompactSnapshot_ResendsInfoPeriodically_SoALostFirstPacketHeals()
    {
        var (world, viewer, everything) = CrowdedScene();
        var session = new SessionState { ClientVersion = 2 };
        var lb = world.GetLeaderboard();
        InterestManager.EncodeFor(100, viewer.GroupId, session, everything, new List<FoodItem>(), lb, world.HalfWidth, world.HalfHeight, Dt); // "lost"

        // A brand-new decoder (client that missed that packet) sees entities without names...
        var late = new SnapshotDecoder { HalfWidth = world.HalfWidth, HalfHeight = world.HalfHeight };
        var missed = late.Decode(InterestManager.EncodeFor(101, viewer.GroupId, session, everything, new List<FoodItem>(), lb, world.HalfWidth, world.HalfHeight, Dt));
        Assert.Contains(missed.Entities, e => !e.HasInfo);

        // ...and learns them within the resend interval.
        var healed = late.Decode(InterestManager.EncodeFor(100 + Protocol.InfoResendTicks, viewer.GroupId, session, everything, new List<FoodItem>(), lb, world.HalfWidth, world.HalfHeight, Dt));
        Assert.All(healed.Entities, e => Assert.True(e.HasInfo));
    }

    [Fact]
    public void OriginalSnapshotFormat_StillDecodesIncludingWhenTheTrailingSectionIsMissing()
    {
        var (world, viewer, everything) = CrowdedScene();
        var packet = Protocol.EncodeSnapshot(3, everything, new List<FoodItem>(), world.GetLeaderboard());

        var full = new SnapshotDecoder().Decode(packet);
        Assert.False(full.IsCompact);
        Assert.Equal(everything.Count, full.Entities.Count);
        Assert.NotEmpty(full.GroupOf);
        Assert.Equal(everything.First(e => e is PlayerEntity).Name, full.Entities.First(e => e.Type == 0).Name);

        // An older server never appended the ownership list: the decoder must not choke on its absence.
        int groupPairs = full.GroupOf.Count;
        var oldServerPacket = packet.Take(packet.Length - 2 - 8 * groupPairs).ToArray();
        var legacy = new SnapshotDecoder().Decode(oldServerPacket);
        Assert.Equal(full.Entities.Count, legacy.Entities.Count);
        Assert.Empty(legacy.GroupOf);
    }

    [Fact]
    public void Decoder_RejectsTruncatedPacketsInsteadOfReturningGarbage()
    {
        var (world, viewer, everything) = CrowdedScene();
        var packet = InterestManager.EncodeFor(5, viewer.GroupId, new SessionState { ClientVersion = 2 }, everything, new List<FoodItem>(), world.GetLeaderboard(), world.HalfWidth, world.HalfHeight, Dt);

        Assert.Throws<EndOfStreamException>(() => new SnapshotDecoder().Decode(packet.Take(packet.Length / 2).ToArray()));
    }

    // ---- Virus feeding ---------------------------------------------------------------------------------------

    [Fact]
    public void FeedingAVirusEnoughSpawnsANewOne_ButNeverBeyondTheCap()
    {
        var world = Arena();
        var virus = world.Viruses.First();
        virus.Position = new Vector2(0, 0);
        var thrower = Spawn(world, 5000f, new Vector2(-300, 300), "Feeder");
        int before = world.Viruses.Count;

        for (int i = 0; i < 200 && world.Viruses.Count == before; i++)
        {
            var pellet = world.AllFood().First(f => f.Velocity == Vector2.Zero && f.EjectDirection == Vector2.Zero);
            pellet.Position = virus.Position;
            pellet.EjectDirection = new Vector2(1, 0);   // thrown pellet (feed-eligible)
            pellet.Velocity = Vector2.Zero;
            world.Tick(Dt);
        }
        Assert.Equal(before + 1, world.Viruses.Count);

        for (int i = 0; i < 600; i++)
        {
            var pellet = world.AllFood().First(f => f.Velocity == Vector2.Zero && f.EjectDirection == Vector2.Zero);
            pellet.Position = world.Viruses.First().Position;
            pellet.EjectDirection = new Vector2(1, 0);
            world.Tick(Dt);
        }
        Assert.True(world.Viruses.Count <= world.VirusCount * Rules.SpikeMaxCountMultiplier);
    }

    // ---- Bots --------------------------------------------------------------------------------------------------

    [Fact]
    public void BotsDodgeSpikesThatWouldPopThem_UnlessEasy()
    {
        Vector2 MoveDir(BotDifficulty difficulty)
        {
            var cfg = new GameConfig { BotDifficulty = difficulty };
            GameConfig.Use(cfg);
            try
            {
                var world = Arena();
                var saw = world.Saws.First();
                saw.Position = new Vector2(100, 0);
                var bot = world.Bots.First();
                bot.Mass = 1000f;                                   // scale ~34: a saw (14) pops it
                bot.Position = new Vector2(100 - (bot.Scale + saw.Scale) / 2f - 4f, 0); // heading straight into it, 4 units from contact
                bot.RoamTarget = new Vector2(400, 0);
                world.Tick(Dt);
                AiBrain.Think(bot, world, new Random(1), Dt);
                return bot.MoveDirection;
            }
            finally { GameConfig.Use(new GameConfig()); }
        }

        Assert.True(MoveDir(BotDifficulty.Normal).X < -0.5f, "a normal bot backs away from the spike");
        Assert.True(MoveDir(BotDifficulty.Hard).X < -0.5f);
        Assert.True(MoveDir(BotDifficulty.Easy).X > 0.5f, "an easy bot walks into it");
    }

    [Fact]
    public void BotDifficulty_ChangesSensingAndSpeed_AndBindsFromConfiguration()
    {
        Assert.True(Rules.BotSenseRange(BotDifficulty.Easy) < Rules.BotSenseRange(BotDifficulty.Normal));
        Assert.True(Rules.BotSenseRange(BotDifficulty.Normal) < Rules.BotSenseRange(BotDifficulty.Hard));
        Assert.True(Rules.BotSpeedFactor(BotDifficulty.Easy) < Rules.BotSpeedFactor(BotDifficulty.Normal));

        var bound = new GameConfig();
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Game:BotDifficulty"] = "Hard", ["Game:PowerupSeconds"] = "12" })
            .Build().GetSection("Game").Bind(bound);
        Assert.Equal(BotDifficulty.Hard, bound.BotDifficulty);
        Assert.Equal(12f, bound.PowerupSeconds);
    }

    // ---- Leaderboard store -----------------------------------------------------------------------------------------

    [Fact]
    public void Leaderboard_KeepsEachNamesBestRunAndFiltersByPeriod()
    {
        var store = new LeaderboardStore();
        var now = DateTime.UtcNow;
        store.Record("Ada", 500, 60, now.AddDays(-10));
        store.Record("Ada", 300, 40, now);
        store.Record("Bob", 450, 50, now);
        store.Record("Tiny", 20, 5, now);          // below the minimum: ignored
        store.Record("NaN", float.NaN, 5, now);    // hostile value: ignored
        store.Record("  ", 900, 5, now);           // no name: ignored

        var all = store.Top(null, 10);
        Assert.Equal(new[] { "Ada", "Bob" }, all.Select(e => e.Name));
        Assert.Equal(500, all[0].Mass);            // Ada's best, once

        var week = store.Top(TimeSpan.FromDays(7), 10);
        Assert.Equal(new[] { "Bob", "Ada" }, week.Select(e => e.Name)); // Ada's 500 is too old for "this week"
        Assert.Equal(300, week[1].Mass);
        Assert.Single(store.Top(null, 1));
    }

    [Fact]
    public void Leaderboard_SurvivesARestart_AndACorruptFileDoesNotStopStartup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "blob-lb-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "leaderboard.json");
        try
        {
            var first = new LeaderboardStore(path);
            first.Record("Ada", 500, 60);
            first.Record("Bob", 250, 30);

            var second = new LeaderboardStore(path);
            Assert.Equal(new[] { "Ada", "Bob" }, second.Top(null, 10).Select(e => e.Name));

            File.WriteAllText(path, "{ this is not json");
            var third = new LeaderboardStore(path);
            Assert.Equal(0, third.Count);
            Assert.True(File.Exists(path + ".corrupt")); // kept for inspection
            third.Record("Cy", 400, 10);                 // and it works again
            Assert.Single(new LeaderboardStore(path).Top(null, 10));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ---- Abuse guard ------------------------------------------------------------------------------------------------------

    [Fact]
    public void AbuseGuard_RateLimitsPacketsRefillsAndRejectsOversizedDatagrams()
    {
        var guard = new AbuseGuard(maxPacketBytes: 100, packetsPerSecond: 10);
        var from = NextEndPoint();
        var t = DateTime.UtcNow;

        Assert.False(guard.AllowPacket(from, 101, t)); // oversized
        int allowed = Enumerable.Range(0, 50).Count(_ => guard.AllowPacket(from, 20, t));
        Assert.Equal(10, allowed);                     // a burst of one second's worth, then throttled
        Assert.False(guard.AllowPacket(from, 20, t));
        Assert.True(guard.AllowPacket(from, 20, t.AddSeconds(0.5))); // refilled
        Assert.True(guard.AllowPacket(NextEndPoint(), 20, t));       // other senders unaffected
    }

    [Fact]
    public void AbuseGuard_LimitsJoinRateAndSessionsPerAddress_ButOnlyPerAddress()
    {
        var guard = new AbuseGuard(joinsPerMinutePerIp: 3, maxSessionsPerIp: 5);
        var a = IPAddress.Parse("10.0.0.1");
        var b = IPAddress.Parse("10.0.0.2");
        var t = DateTime.UtcNow;

        Assert.True(guard.AllowJoin(a, 0, t));
        Assert.True(guard.AllowJoin(a, 1, t));
        Assert.True(guard.AllowJoin(a, 2, t));
        Assert.False(guard.AllowJoin(a, 3, t));               // 4th join inside a minute
        Assert.True(guard.AllowJoin(a, 3, t.AddSeconds(61))); // window passed
        Assert.True(guard.AllowJoin(b, 0, t));                // different address unaffected
        Assert.False(guard.AllowJoin(b, 5, t));               // session cap

        guard.Prune(t.AddMinutes(10));                        // no throw, tables drain
        Assert.True(guard.AllowJoin(a, 0, t.AddMinutes(10)));
    }

    // ---- Metrics ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Metrics_ReportTickTimesAndRenderPrometheusText()
    {
        for (int i = 0; i < 100; i++) ServerMetrics.Tick(i < 99 ? 2.0 : 50.0);
        ServerMetrics.Snapshot(120);
        ServerMetrics.Snapshot(300);

        var report = ServerMetrics.Read();
        Assert.True(report.TickMaxMs >= 50.0 - 0.001 || report.TickMaxMs > 0);
        Assert.True(report.SnapshotMaxBytes >= 120);
        Assert.True(report.SlowTicks >= 1);

        var text = ServerMetrics.ToPrometheus(rooms: 2, players: 7);
        Assert.Contains("blob_rooms 2", text);
        Assert.Contains("blob_players 7", text);
        Assert.Contains("# TYPE blob_tick_p99_ms gauge", text);
        Assert.DoesNotContain(",", text.Split('\n').First(l => l.StartsWith("blob_rooms ")));
    }
}
