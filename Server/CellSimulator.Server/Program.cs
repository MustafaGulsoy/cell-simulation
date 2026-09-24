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

builder.Services.AddSingleton(new RoomManager(mapSize));
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
    food = r.World.AllFood().Count,
    halfMapSize = r.World.HalfMapSize,
    halfWidth = r.World.HalfWidth,
    halfHeight = r.World.HalfHeight,
    mapSize = r.World.Size.ToString(),
}));

app.MapGet("/leaderboard", (RoomManager rooms) => rooms.Rooms.Select(r => new
{
    roomId = r.Id,
    entries = r.World.GetLeaderboard().Select(e => new { name = e.Name, mass = e.Mass }),
}));

app.Run();
