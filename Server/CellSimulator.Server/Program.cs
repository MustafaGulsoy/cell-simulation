using CellSimulator.Server;
using CellSimulator.Server.Game;
using CellSimulator.Server.Net;

var builder = WebApplication.CreateBuilder(args);

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
    food = r.World.AllFood().Count,
    halfMapSize = r.World.HalfMapSize,
}));

app.MapGet("/leaderboard", (RoomManager rooms) => rooms.Rooms.Select(r => new
{
    roomId = r.Id,
    entries = r.World.GetLeaderboard().Select(e => new { name = e.Name, mass = e.Mass }),
}));

app.Run();
