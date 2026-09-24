using System.Net;
using System.Numerics;
using CellSimulator.Server.Game;
using Xunit;

namespace CellSimulator.Server.Tests;

// Regression tests for "blob jitters while standing still" and "split pieces sink into each
// other": both came from steering at full speed toward a point with no arrival behaviour, fighting
// a partial overlap correction every tick. Assertions are on the state at the END of each tick,
// which is exactly what gets broadcast to clients.
public class MovementTests
{
    private const float Dt = 1f / 30f;
    private const float Eps = 0.001f;

    private static (GameWorld World, PlayerEntity Player) NewSplitPlayer(float mass, int splits)
    {
        var world = new GameWorld(MapSize.Small);
        var player = world.AddPlayer("p", new IPEndPoint(IPAddress.Loopback, 20000));
        player.Mass = mass;
        player.Position = Vector2.Zero;
        world.SetPlayerInput(player.GroupId, new Vector2(1f, 0f));
        world.Tick(Dt);
        for (int i = 0; i < splits; i++)
        {
            world.SplitPlayer(player.GroupId);
            Thread.Sleep(Rules.SplitCooldown + TimeSpan.FromMilliseconds(20));
        }
        for (int i = 0; i < 30; i++) world.Tick(Dt); // let the launch lerps finish
        return (world, player);
    }

    private static List<PlayerEntity> Pieces(GameWorld world, uint groupId) =>
        world.Players.Where(p => p.GroupId == groupId).ToList();

    private static float WorstOverlap(List<PlayerEntity> pieces)
    {
        float worst = 0f;
        for (int a = 0; a < pieces.Count; a++)
            for (int b = a + 1; b < pieces.Count; b++)
                worst = MathF.Max(worst, (pieces[a].Scale + pieces[b].Scale) / 2f - Vector2.Distance(pieces[a].Position, pieces[b].Position));
        return worst;
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(100f)]
    [InlineData(2000f)]
    public void ReleasedJoystick_BlobStopsAndStaysPut(float mass)
    {
        var world = new GameWorld(MapSize.Small);
        var p = world.AddPlayer("p", new IPEndPoint(IPAddress.Loopback, 20001));
        p.Mass = mass;
        p.Position = Vector2.Zero;

        world.SetPlayerInput(p.GroupId, new Vector2(1f, 0f));
        for (int i = 0; i < 90; i++) world.Tick(Dt);
        Assert.True(p.Position.X > 1f); // it did move while held

        world.SetPlayerInput(p.GroupId, Vector2.Zero);
        var stoppedAt = p.Position;
        for (int i = 0; i < 90; i++)
        {
            world.Tick(Dt);
            Assert.Equal(stoppedAt, p.Position); // no coasting toward a stale target, no dithering
        }
    }

    [Fact]
    public void SplitPiecesAtRest_DoNotMoveOrOverlap()
    {
        var (world, player) = NewSplitPlayer(400f, splits: 2);
        world.SetPlayerInput(player.GroupId, Vector2.Zero);
        var pieces = Pieces(world, player.GroupId);
        Assert.True(pieces.Count >= 3);
        world.Tick(Dt); // absorb the one tick where the stale cursor state could still apply

        var before = pieces.ToDictionary(x => x.Id, x => x.Position);
        for (int i = 0; i < 90; i++)
        {
            world.Tick(Dt);
            foreach (var piece in pieces) Assert.True(Vector2.Distance(before[piece.Id], piece.Position) < Eps, "piece jittered while idle");
            Assert.True(WorstOverlap(pieces) < Eps, "split pieces overlap");
        }
    }

    [Fact]
    public void SplitPiecesWhileMoving_NeverOverlapOnceLaunchSettles()
    {
        var (world, player) = NewSplitPlayer(400f, splits: 2);
        var pieces = Pieces(world, player.GroupId);
        world.SetPlayerInput(player.GroupId, new Vector2(0f, 1f));

        for (int i = 0; i < 90; i++)
        {
            world.Tick(Dt);
            Assert.True(WorstOverlap(pieces) < Eps, "split pieces overlap while moving");
        }
    }

    [Fact]
    public void SplitPieces_RecombineAtRestOnceMergeEligible()
    {
        var (world, player) = NewSplitPlayer(400f, splits: 1);
        world.SetPlayerInput(player.GroupId, Vector2.Zero);
        float massBefore = Pieces(world, player.GroupId).Sum(p => p.Mass);
        foreach (var piece in Pieces(world, player.GroupId)) piece.MergeEligibleUtc = DateTime.MinValue; // "15s passed"

        for (int i = 0; i < 300 && Pieces(world, player.GroupId).Count > 1; i++) world.Tick(Dt);

        var merged = Pieces(world, player.GroupId);
        Assert.Single(merged);
        Assert.InRange(merged[0].Mass, massBefore - 2f, massBefore); // only passive mass decay may shave a little
    }
}
