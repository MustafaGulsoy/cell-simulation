using System.Net;
using System.Numerics;

namespace CellSimulator.Server.Game;

/// <summary>
/// Holds all authoritative game state and advances it one tick at a time.
/// All public methods are safe to call concurrently (single coarse lock) - entity counts
/// are small (a handful of players/bots, a few hundred food) so this is plenty fast.
/// </summary>
public sealed class GameWorld
{
    private readonly object _gate = new();
    private readonly Random _rng = new();

    private readonly Dictionary<uint, PlayerEntity> _players = new();
    private readonly Dictionary<uint, AiEntity> _bots = new();
    private readonly Dictionary<uint, FoodItem> _food = new();
    private readonly List<FoodItem> _foodChangedThisTick = new();

    private uint _nextId = 1;

    public int HalfMapSize { get; }
    public int BotCount { get; }
    public int FoodCount { get; }
    public int PlayerCapacity { get; }

    public GameWorld(MapSize mapSize)
    {
        int side = MapSizes.SideLength(mapSize);
        HalfMapSize = side / 2;
        BotCount = Math.Max(1, side / 60);
        FoodCount = Math.Max(50, side * 7 - 1000);
        PlayerCapacity = Math.Max(2, side / 20);
    }

    public void Initialize()
    {
        lock (_gate)
        {
            for (int i = 0; i < FoodCount; i++)
            {
                var food = new FoodItem { Id = _nextId++, Position = RandomPosition() };
                _food[food.Id] = food;
            }

            for (int i = 0; i < BotCount; i++)
            {
                SpawnBot();
            }
        }
    }

    private Vector2 RandomPosition()
    {
        float x = (float)(_rng.NextDouble() * 2 - 1) * HalfMapSize;
        float y = (float)(_rng.NextDouble() * 2 - 1) * HalfMapSize;
        return new Vector2(x, y);
    }

    private void SpawnBot()
    {
        var bot = new AiEntity
        {
            Id = _nextId++,
            Position = RandomPosition(),
            Mass = Rules.MassMin,
            Color = Rgba.Random(_rng),
            Name = "Bot" + _rng.Next(1000, 9999),
            RoamTarget = RandomPosition(),
        };
        _bots[bot.Id] = bot;
    }

    public PlayerEntity AddPlayer(string name, IPEndPoint endPoint)
    {
        lock (_gate)
        {
            var player = new PlayerEntity
            {
                Id = _nextId++,
                Position = RandomPosition(),
                Mass = Rules.MassMin,
                Color = Rgba.Random(_rng),
                Name = string.IsNullOrWhiteSpace(name) ? "Unnamed" : name,
                EndPoint = endPoint,
            };
            _players[player.Id] = player;
            return player;
        }
    }

    public void RemovePlayer(uint id)
    {
        lock (_gate)
        {
            _players.Remove(id);
        }
    }

    public PlayerEntity? FindPlayerByEndPoint(IPEndPoint endPoint)
    {
        lock (_gate)
        {
            foreach (var p in _players.Values)
            {
                if (p.EndPoint.Equals(endPoint)) return p;
            }
            return null;
        }
    }

    public void SetPlayerInput(uint id, Vector2 direction)
    {
        lock (_gate)
        {
            if (_players.TryGetValue(id, out var p))
            {
                p.InputDirection = direction;
                p.LastSeenUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>Removes players that haven't sent input in a while (they closed the client without a clean disconnect).</summary>
    public List<uint> RemoveStalePlayers(TimeSpan timeout)
    {
        lock (_gate)
        {
            var stale = new List<uint>();
            var cutoff = DateTime.UtcNow - timeout;
            foreach (var p in _players.Values)
            {
                if (p.LastSeenUtc < cutoff) stale.Add(p.Id);
            }
            foreach (var id in stale) _players.Remove(id);
            return stale;
        }
    }

    public void Tick(float dt)
    {
        lock (_gate)
        {
            _foodChangedThisTick.Clear();

            MoveEntity(_players.Values, dt);
            foreach (var bot in _bots.Values)
            {
                AiBrain.Think(bot, this, _rng, dt);
            }
            MoveEntity(_bots.Values, dt);

            ResolveFoodEating();
            ResolveBlobEating();
        }
    }

    private void MoveEntity<T>(IEnumerable<T> entities, float dt) where T : Entity
    {
        foreach (var e in entities)
        {
            Vector2 dir = e is PlayerEntity p ? p.InputDirection : e is AiEntity a ? a.MoveDirection : Vector2.Zero;
            if (dir != Vector2.Zero)
            {
                if (dir.LengthSquared() > 1f) dir = Vector2.Normalize(dir);
                float speed = Rules.MovementSpeedForScale(e.Scale);
                e.Position += dir * speed * dt;
            }

            e.Position = new Vector2(
                Math.Clamp(e.Position.X, -HalfMapSize, HalfMapSize),
                Math.Clamp(e.Position.Y, -HalfMapSize, HalfMapSize));
        }
    }

    // Food-lookup grid: checking every entity against every food item is O(entities * food)
    // (5200 distance checks/tick on a full Small room) which stacks up badly once many rooms
    // are ticking on a couple of shared CPU cores. Bucketing food into cells lets each entity
    // only check food near it - the common case (a small blob, cell size >> its eating radius)
    // drops to ~O(1) buckets instead of scanning all 400 food items.
    private const float FoodGridCellSize = 10f;

    private static (int X, int Y) CellOf(Vector2 pos) =>
        ((int)MathF.Floor(pos.X / FoodGridCellSize), (int)MathF.Floor(pos.Y / FoodGridCellSize));

    private Dictionary<(int X, int Y), List<FoodItem>> BuildFoodGrid()
    {
        var grid = new Dictionary<(int X, int Y), List<FoodItem>>();
        foreach (var food in _food.Values)
        {
            var cell = CellOf(food.Position);
            if (!grid.TryGetValue(cell, out var list))
            {
                list = new List<FoodItem>();
                grid[cell] = list;
            }
            list.Add(food);
        }
        return grid;
    }

    private void ResolveFoodEating()
    {
        var grid = BuildFoodGrid();

        foreach (var e in AllBlobs())
        {
            float eatRadius = e.Scale / 2f + FoodItem.Radius;
            int ring = Math.Max(1, (int)MathF.Ceiling(eatRadius / FoodGridCellSize));
            var (cx, cy) = CellOf(e.Position);

            FoodItem? eaten = null;
            for (int dx = -ring; dx <= ring && eaten == null; dx++)
            {
                for (int dy = -ring; dy <= ring && eaten == null; dy++)
                {
                    if (!grid.TryGetValue((cx + dx, cy + dy), out var bucket)) continue;

                    foreach (var food in bucket)
                    {
                        if (Vector2.Distance(e.Position, food.Position) < eatRadius)
                        {
                            eaten = food;
                            break; // one food per entity per tick is plenty
                        }
                    }
                }
            }

            if (eaten != null)
            {
                e.Mass = Rules.ClampMass(e.Mass + Rules.FoodMassGain);
                eaten.Position = RandomPosition();
                _foodChangedThisTick.Add(eaten);
            }
        }
    }

    private void ResolveBlobEating()
    {
        var all = AllBlobs().ToList();
        for (int i = 0; i < all.Count; i++)
        {
            var a = all[i];
            for (int j = i + 1; j < all.Count; j++)
            {
                var b = all[j];
                float dist = Vector2.Distance(a.Position, b.Position);
                if (dist > Math.Max(a.Scale, b.Scale) / 2f) continue;

                if (Rules.CanEat(a.Scale, b.Scale))
                {
                    Devour(a, b);
                }
                else if (Rules.CanEat(b.Scale, a.Scale))
                {
                    Devour(b, a);
                }
            }
        }
    }

    private void Devour(Entity winner, Entity loser)
    {
        winner.Mass = Rules.ClampMass(winner.Mass + loser.Mass);
        RespawnAsNew(loser);
    }

    private void RespawnAsNew(Entity e)
    {
        e.Mass = Rules.MassMin;
        e.Position = RandomPosition();
        if (e is AiEntity ai)
        {
            ai.State = AiState.Roaming;
            ai.TargetId = null;
            ai.ThreatId = null;
            ai.RoamTarget = RandomPosition();
        }
    }

    private IEnumerable<Entity> AllBlobs()
    {
        foreach (var p in _players.Values) yield return p;
        foreach (var a in _bots.Values) yield return a;
    }

    public IReadOnlyCollection<PlayerEntity> Players
    {
        get { lock (_gate) { return _players.Values.ToArray(); } }
    }

    public IReadOnlyCollection<AiEntity> Bots
    {
        get { lock (_gate) { return _bots.Values.ToArray(); } }
    }

    public IReadOnlyCollection<FoodItem> AllFood()
    {
        lock (_gate) { return _food.Values.ToArray(); }
    }

    public List<FoodItem> DrainChangedFood()
    {
        lock (_gate)
        {
            var copy = new List<FoodItem>(_foodChangedThisTick);
            return copy;
        }
    }

    public List<(string Name, float Mass)> GetLeaderboard(int top = 5)
    {
        lock (_gate)
        {
            return AllBlobs()
                .OrderByDescending(e => e.Mass)
                .Take(top)
                .Select(e => (e.Name, e.Mass))
                .ToList();
        }
    }

    public Entity? FindBlob(uint id)
    {
        lock (_gate)
        {
            if (_players.TryGetValue(id, out var p)) return p;
            if (_bots.TryGetValue(id, out var a)) return a;
            return null;
        }
    }
}
