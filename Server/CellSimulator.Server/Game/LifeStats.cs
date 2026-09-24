using System.Net;

namespace CellSimulator.Server.Game;

/// <summary>Running numbers for one player's current life (a "life" ends when their last cell is eaten).</summary>
public sealed class LifeStats
{
    public DateTime StartUtc = DateTime.UtcNow;
    public float PeakMass;
    public int FoodEaten;
    public int BlobsEaten;
    public int SpikesHit;
}

public enum DeathReason
{
    Eaten,
    Disconnected,
}

/// <summary>Everything about a finished life; GameWorld queues these (DrainDeaths) and the game loop
/// turns them into the Died packet for the player and a leaderboard entry.</summary>
public sealed record DeathInfo(
    uint GroupId,
    string PlayerName,
    IPEndPoint EndPoint,
    DeathReason Reason,
    string KillerName,
    float PeakMass,
    float SurvivedSeconds,
    int FoodEaten,
    int BlobsEaten,
    int SpikesHit);
