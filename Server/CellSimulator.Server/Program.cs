using CellSimulator.Server;
using CellSimulator.Server.Game;
using CellSimulator.Server.Net;

var builder = WebApplication.CreateBuilder(args);

// Gameplay tuning (split/merge/speed/spikes/map size) - see GameConfig. Override any value from
// appsettings.json's "Game" section or an env var such as Game__SplitForce=1.3; no rebuild needed.
var gameConfig = new GameConfig();
builder.Configuration.GetSection("Game").Bind(gameConfig);
GameConfig.Use(gameConfig);

var mapSizeName = builder.Configuration.GetValue("Server:MapSize", "Small")!;
var mapSize = Enum.Parse<MapSize>(mapSizeName, ignoreCase: true);

// Where the persistent leaderboard lives (mount a volume here in Docker so it survives redeploys).
var dataDir = builder.Configuration.GetValue("Server:DataDir", "data")!;

builder.Services.AddSingleton(new RoomManager(mapSize));
builder.Services.AddSingleton(new LeaderboardStore(Path.Combine(dataDir, "leaderboard.json")));
builder.Services.AddSingleton<UdpServerService>();
builder.Services.AddHostedService<UdpServerService>(sp => sp.GetRequiredService<UdpServerService>());
builder.Services.AddHostedService<GameLoopService>();

var app = builder.Build();

app.MapGet("/health", () => "ok");

app.MapGet("/stats", (RoomManager rooms) => rooms.Rooms.Select(r => new
{
    roomId = r.Id,
    players = r.World.Players.Count,
    capacity = r.World.PlayerCapacity,
    bots = r.World.Bots.Count,
    viruses = r.World.Viruses.Count,
    saws = r.World.Saws.Count,
    powerups = r.World.Powerups.Count,
    food = r.World.AllFood().Count,
    halfMapSize = r.World.HalfMapSize,
    halfWidth = r.World.HalfWidth,
    halfHeight = r.World.HalfHeight,
    mapSize = r.World.Size.ToString(),
}));

// Live per-room top 5 (who is biggest right now).
app.MapGet("/leaderboard", (RoomManager rooms) => rooms.Rooms.Select(r => new
{
    roomId = r.Id,
    entries = r.World.GetLeaderboard().Select(e => new { name = e.Name, mass = e.Mass }),
}));

// Best runs ever recorded (survives restarts): /leaderboard/top?period=day|week|all&limit=10
app.MapGet("/leaderboard/top", (LeaderboardStore store, string? period, int? limit) =>
{
    TimeSpan? window = period?.ToLowerInvariant() switch
    {
        "day" => TimeSpan.FromDays(1),
        "week" => TimeSpan.FromDays(7),
        _ => null,
    };
    return store.Top(window, limit ?? 10).Select(e => new { name = e.Name, mass = e.Mass, survivedSeconds = e.SurvivedSeconds, utc = e.Utc });
});

// Health numbers for scraping (Prometheus text format) - tick time vs the 33 ms budget, snapshot sizes, counters.
app.MapGet("/metrics", (RoomManager rooms) =>
{
    var all = rooms.Rooms;
    return Results.Text(ServerMetrics.ToPrometheus(all.Count, all.Sum(r => r.World.Players.Count)), "text/plain; version=0.0.4");
});

app.Run();
