using System.Net;
using System.Numerics;
using CellSimulator.Server.Game;
using Xunit;

namespace CellSimulator.Server.Tests;

// Team mode: GameWorld(mapSize, teamsEnabled: true). Players get a balanced TeamId/colour on join,
// friendly fire is off between teammates, and cross-team eating is unaffected.
public class TeamModeTests
{
    private const float Dt = 1f / 30f;
    private static int _port = 50000;
    private static IPEndPoint NextEndPoint() => new(IPAddress.Loopback, Interlocked.Increment(ref _port));

    private static PlayerEntity Spawn(GameWorld world, float mass, Vector2 at)
    {
        var p = world.AddPlayer("p", NextEndPoint());
        p.Mass = mass;
        p.Position = at;
        return p;
    }

    [Fact]
    public void Join_AssignsBalancedTeamsAndTheirFixedColour()
    {
        var world = new GameWorld(MapSize.Small, teamsEnabled: true);
        var a = world.AddPlayer("a", NextEndPoint());
        var b = world.AddPlayer("b", NextEndPoint());
        var c = world.AddPlayer("c", NextEndPoint());
        var d = world.AddPlayer("d", NextEndPoint());

        Assert.Equal(new byte[] { 1, 2, 1, 2 }, new[] { a.TeamId, b.TeamId, c.TeamId, d.TeamId });
        Assert.Equal(Teams.ColorOf(a.TeamId).R, a.Color.R);
        Assert.Equal(Teams.ColorOf(b.TeamId).R, b.Color.R);
    }

    [Fact]
    public void FfaWorld_NeverAssignsATeam()
    {
        var world = new GameWorld(MapSize.Small);
        var a = world.AddPlayer("a", NextEndPoint());
        Assert.Equal(0, a.TeamId);
    }

    [Fact]
    public void Teammates_DoNotEatOrBlockEachOther()
    {
        var world = new GameWorld(MapSize.Small, teamsEnabled: true);
        var big = Spawn(world, 500f, Vector2.Zero);
        var small = Spawn(world, 50f, Vector2.Zero); // same spot - would devour instantly if eligible

        // Force them onto the same team regardless of the join-order balancing.
        small.TeamId = big.TeamId;

        world.Tick(Dt);

        Assert.Equal(500f, big.Mass, precision: 1);
        Assert.Equal(50f, small.Mass, precision: 1);
    }

    [Fact]
    public void OpposingTeams_StillEatEachOther()
    {
        var world = new GameWorld(MapSize.Small, teamsEnabled: true);
        var big = Spawn(world, 500f, Vector2.Zero);
        var small = Spawn(world, 50f, Vector2.Zero);
        Assert.NotEqual(big.TeamId, small.TeamId); // guaranteed by the 2-team balanced join order

        world.Tick(Dt);

        Assert.Equal(550f, big.Mass, precision: 1);
        Assert.Equal(Rules.MassMin, small.Mass, precision: 1); // eaten and respawned
    }

    [Fact]
    public void SplitClone_InheritsItsSourcesTeam()
    {
        var world = new GameWorld(MapSize.Small, teamsEnabled: true);
        var player = Spawn(world, 400f, Vector2.Zero);
        world.SplitPlayer(player.GroupId);

        var clone = world.Players.Single(p => p.GroupId == player.GroupId && p.Id != p.GroupId);
        Assert.Equal(player.TeamId, clone.TeamId);
    }
}
