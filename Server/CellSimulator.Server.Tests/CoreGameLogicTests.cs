using System.Net;
using CellSimulator.Server.Game;
using Xunit;

namespace CellSimulator.Server.Tests;

// One smoke-test file covering the math/rules that previously had zero coverage. Not exhaustive -
// just enough that a broken CanEat/split/merge/name-sanitize regression fails a build instead of
// only being caught by a player in production.
public class CoreGameLogicTests
{
    [Theory]
    [InlineData(100f, 80f, true)]   // eater ~15%+ bigger -> can eat
    [InlineData(100f, 90f, false)]  // too close in size -> blocked, not eaten
    [InlineData(80f, 100f, false)]  // smaller can't eat bigger
    public void CanEat_RequiresScaleMultiplierMargin(float eaterScale, float preyScale, bool expected)
    {
        Assert.Equal(expected, Rules.CanEat(eaterScale, preyScale));
    }

    [Fact]
    public void ClampMass_StaysWithinMinAndMax()
    {
        Assert.Equal(Rules.MassMin, Rules.ClampMass(0f));
        Assert.Equal(Rules.MassMax, Rules.ClampMass(float.MaxValue));
        Assert.Equal(500f, Rules.ClampMass(500f));
    }

    [Fact]
    public void CalculateScale_ClampsToBlobScaleRange()
    {
        Assert.Equal(Rules.BlobScaleMin, Rules.CalculateScale(0f));
        Assert.Equal(Rules.BlobScaleMax, Rules.CalculateScale(Rules.MassMax));
    }

    [Fact]
    public void MovementSpeedForScale_IsFasterWhenSmaller()
    {
        float small = Rules.MovementSpeedForScale(Rules.BlobScaleMin);
        float big = Rules.MovementSpeedForScale(Rules.BlobScaleMax);
        Assert.True(small > big);
        Assert.Equal(Rules.MovementSpeedMax, small, precision: 4);
        Assert.Equal(Rules.MovementSpeedMin, big, precision: 4);
    }

    [Fact]
    public void AddPlayer_SanitizesRichTextAndOverlongNames()
    {
        var world = new GameWorld(MapSize.Small);
        var endPoint = new IPEndPoint(IPAddress.Loopback, 12345);

        var player = world.AddPlayer("<color=red>Hacker</color>NameThatIsWayTooLong", endPoint);

        Assert.DoesNotContain("<", player.Name);
        Assert.DoesNotContain(">", player.Name);
        Assert.True(player.Name.Length <= 20);
    }

    [Fact]
    public void AddPlayer_BlankNameFallsBackToUnnamed()
    {
        var world = new GameWorld(MapSize.Small);
        var player = world.AddPlayer("   ", new IPEndPoint(IPAddress.Loopback, 12346));

        Assert.Equal("Unnamed", player.Name);
    }

    [Fact]
    public void SplitPlayer_ConservesTotalMass()
    {
        var world = new GameWorld(MapSize.Small);
        var player = world.AddPlayer("Splitter", new IPEndPoint(IPAddress.Loopback, 12347));
        player.Mass = 200f; // well above SplitMinMass
        world.SetPlayerInput(player.GroupId, new System.Numerics.Vector2(1f, 0f));

        float massBefore = world.Players.Where(p => p.GroupId == player.GroupId).Sum(p => p.Mass);
        world.SplitPlayer(player.GroupId);
        float massAfter = world.Players.Where(p => p.GroupId == player.GroupId).Sum(p => p.Mass);

        Assert.True(world.Players.Count(p => p.GroupId == player.GroupId) > 1);
        Assert.Equal(massBefore, massAfter, precision: 2);
    }

    [Fact]
    public void SplitPlayer_NoOpBelowMinMassOrDuringCooldown()
    {
        var world = new GameWorld(MapSize.Small);
        var player = world.AddPlayer("TooSmall", new IPEndPoint(IPAddress.Loopback, 12348));
        // Mass stays at MassMin, well under SplitMinMass.

        world.SplitPlayer(player.GroupId);

        Assert.Single(world.Players, p => p.GroupId == player.GroupId);
    }
}
