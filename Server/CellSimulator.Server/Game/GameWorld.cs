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
    private readonly Dictionary<uint, VirusEntity> _viruses = new();
    private readonly Dictionary<uint, SawEntity> _saws = new();
    private readonly Dictionary<uint, FoodItem> _food = new();
    private readonly List<FoodItem> _foodChangedThisTick = new();

    private readonly Dictionary<uint, DateTime> _lastSplitUtc = new();
    private readonly Dictionary<uint, DateTime> _lastEjectUtc = new();

    // Per-group "cursor" point that the joystick moves - every piece the player owns steers
    // toward this shared point instead of moving in lockstep parallel to the raw input direction,
    // so pieces actively gather back together (agar.io-style: you move a point, cells chase it)
    // rather than just drifting in formation forever.
    private readonly Dictionary<uint, Vector2> _groupTargets = new();

    private uint _nextId = 1;

    public MapSize Size { get; }
    public int HalfMapSize { get; }
    public int BotCount { get; }
    public int FoodCount { get; }
    public int VirusCount { get; }
    public int SawCount { get; }
    public int PlayerCapacity { get; }

    public GameWorld(MapSize mapSize)
    {
        int side = MapSizes.SideLength(mapSize);
        Size = mapSize;
        HalfMapSize = side / 2;
        BotCount = Math.Max(1, side / 60);
        FoodCount = Math.Max(50, side * 7 - 1000);
        VirusCount = Math.Max(3, side / 150);
        SawCount = Math.Max(2, side / 200);
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

            for (int i = 0; i < VirusCount; i++)
            {
                SpawnVirus();
            }

            for (int i = 0; i < SawCount; i++)
            {
                SpawnSaw();
            }
        }
    }

    private void SpawnVirus()
    {
        var virus = new VirusEntity
        {
            Id = _nextId++,
            Position = RandomPosition(),
            Mass = Rules.VirusMass,
            Color = Rules.VirusColor,
            Name = "Virus",
        };
        _viruses[virus.Id] = virus;
    }

    private void SpawnSaw()
    {
        var saw = new SawEntity
        {
            Id = _nextId++,
            Position = RandomPosition(),
            Mass = Rules.SawMass,
            Color = Rules.SawColor,
            Name = "Saw",
            FeedThreshold = _rng.Next(Rules.SawFeedThresholdMin, Rules.SawFeedThresholdMax + 1),
        };
        _saws[saw.Id] = saw;
    }

    private void SpawnSawAt(Vector2 position)
    {
        var saw = new SawEntity
        {
            Id = _nextId++,
            Position = ClampToMap(position),
            Mass = Rules.SawMass,
            Color = Rules.SawColor,
            Name = "Saw",
            FeedThreshold = _rng.Next(Rules.SawFeedThresholdMin, Rules.SawFeedThresholdMax + 1),
        };
        _saws[saw.Id] = saw;
    }

    private Vector2 RandomPosition()
    {
        float x = (float)(_rng.NextDouble() * 2 - 1) * HalfMapSize;
        float y = (float)(_rng.NextDouble() * 2 - 1) * HalfMapSize;
        return new Vector2(x, y);
    }

    private Vector2 RandomUnitVector()
    {
        float angle = (float)(_rng.NextDouble() * Math.PI * 2);
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
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
            player.GroupId = player.Id;
            _players[player.Id] = player;
            _groupTargets[player.GroupId] = player.Position;
            return player;
        }
    }

    public void RemovePlayer(uint id)
    {
        lock (_gate)
        {
            // id is the group's original/primary Id (RoomManager only ever tracks that one in
            // Sessions), so a disconnect must drop every split piece the player currently owns.
            var toRemove = _players.Values.Where(p => p.GroupId == id).Select(p => p.Id).ToList();
            foreach (var pid in toRemove) _players.Remove(pid);
            _lastSplitUtc.Remove(id);
            _lastEjectUtc.Remove(id);
            _groupTargets.Remove(id);
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

    /// <summary>groupId is the session's primary/original entity Id. Every piece the player
    /// currently owns shares that direction (agar-style: no independent per-cell aim).</summary>
    public void SetPlayerInput(uint groupId, Vector2 direction)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            foreach (var p in _players.Values)
            {
                if (p.GroupId != groupId) continue;
                p.InputDirection = direction;
                p.LastSeenUtc = now;
            }
        }
    }

    /// <summary>Splits every eligible cell the group owns at once (agar-style), capped at
    /// MaxPiecesPerPlayer total. No-op if nothing is big enough or the group is on cooldown.</summary>
    public void SplitPlayer(uint groupId)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_lastSplitUtc.TryGetValue(groupId, out var last) && now - last < Rules.SplitCooldown) return;

            var pieces = _players.Values.Where(p => p.GroupId == groupId).ToList();
            int pieceCount = pieces.Count;
            if (pieceCount == 0) return;

            var toSplit = pieces.Where(p => p.Mass >= Rules.SplitMinMass).ToList();
            if (toSplit.Count == 0) return;

            foreach (var piece in toSplit)
            {
                if (pieceCount >= Rules.MaxPiecesPerPlayer) break;
                SpawnSplitPiece(piece, groupId);
                pieceCount++;
            }

            _lastSplitUtc[groupId] = now;
        }
    }

    /// <summary>Ejects a small pellet of mass from every cell the group owns, in that cell's
    /// current move direction. Server-validated: mass floor enforced here, not trusted from client.</summary>
    public void EjectMass(uint groupId)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_lastEjectUtc.TryGetValue(groupId, out var last) && now - last < Rules.EjectCooldown) return;

            bool ejectedAny = false;
            foreach (var piece in _players.Values.Where(p => p.GroupId == groupId))
            {
                if (piece.Mass < Rules.EjectMinMass) continue;

                var dir = piece.InputDirection != Vector2.Zero ? SafeDir(piece.InputDirection) : new Vector2(1f, 0f);
                piece.Mass = Rules.ClampMass(piece.Mass - Rules.EjectMassAmount);

                var pellet = new FoodItem
                {
                    Id = _nextId++,
                    Position = piece.Position + dir * (piece.Scale / 2f + FoodItem.Radius),
                    Velocity = dir * Rules.EjectSpeed,
                    EjectDirection = dir,
                };
                _food[pellet.Id] = pellet;
                _foodChangedThisTick.Add(pellet);
                ejectedAny = true;
            }

            if (ejectedAny) _lastEjectUtc[groupId] = now;
        }
    }

    private void SpawnSplitPiece(PlayerEntity piece, uint groupId)
    {
        var dir = piece.InputDirection != Vector2.Zero ? SafeDir(piece.InputDirection) : new Vector2(1f, 0f);
        float newMass = Math.Max(Rules.MassMin, piece.Mass / 2f);
        piece.Mass = newMass;

        var mergeAt = DateTime.UtcNow.AddSeconds(Rules.MergeCooldownSeconds);
        var clone = new PlayerEntity
        {
            Id = _nextId++,
            GroupId = groupId,
            Position = piece.Position,
            Mass = newMass,
            Color = piece.Color,
            Name = piece.Name,
            EndPoint = piece.EndPoint,
            InputDirection = piece.InputDirection,
            LastSeenUtc = piece.LastSeenUtc,
            MergeEligibleUtc = mergeAt,
        };
        piece.MergeEligibleUtc = mergeAt;
        StartLaunch(piece, dir);
        StartLaunch(clone, dir);
        _players[clone.Id] = clone;
    }

    private static Vector2 SafeDir(Vector2 v) => v.LengthSquared() > 0.0001f ? Vector2.Normalize(v) : Vector2.Zero;

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

            AdvanceGroupTargets(dt);
            MoveEntity(_players.Values, dt);
            foreach (var bot in _bots.Values)
            {
                AiBrain.Think(bot, this, _rng, dt);
            }
            MoveEntity(_bots.Values, dt);

            ResolveSplitSeparation(dt);
            MoveFood(dt);
            ResolveSawFeeding();
            ResolveVirusCollisions();
            ResolveSawCollisions();
            ResolveFoodEating();
            ResolveBlobEating();
            ResolveMerges();
        }
    }

    /// <summary>Moves each group's shared "cursor" point by its current input direction (any
    /// piece's InputDirection - they're all kept in sync by SetPlayerInput). Pieces steer toward
    /// this point in MoveEntity rather than moving directly along the raw input direction.</summary>
    private void AdvanceGroupTargets(float dt)
    {
        foreach (var group in _players.Values.GroupBy(p => p.GroupId))
        {
            var rep = group.First();
            if (!_groupTargets.TryGetValue(rep.GroupId, out var target)) target = rep.Position;

            if (rep.InputDirection != Vector2.Zero)
            {
                var dir = rep.InputDirection.LengthSquared() > 1f ? Vector2.Normalize(rep.InputDirection) : rep.InputDirection;
                target = ClampToMap(target + dir * Rules.MovementSpeedMax * dt);
            }

            _groupTargets[rep.GroupId] = target;
        }
    }

    /// <summary>Starts the initial "just been split/popped" lerp launch for one entity - see
    /// Entity.IsLaunching. Distance (and so effective speed, since duration is fixed) scales with
    /// the entity's own current size.</summary>
    private void StartLaunch(Entity e, Vector2 dir)
    {
        float distance = Rules.SplitLaunchBaseDistance + e.Scale * Rules.SplitLaunchDistancePerScale;
        e.LaunchStart = e.Position;
        e.LaunchTarget = ClampToMap(e.Position + dir * distance);
        e.LaunchElapsed = 0f;
        e.IsLaunching = true;
    }

    private void MoveEntity<T>(IEnumerable<T> entities, float dt) where T : Entity
    {
        foreach (var e in entities)
        {
            if (e.IsLaunching)
            {
                e.LaunchElapsed += dt;
                float t = Math.Clamp(e.LaunchElapsed / Rules.SplitLaunchDuration, 0f, 1f);
                e.Position = Vector2.Lerp(e.LaunchStart, e.LaunchTarget, Rules.SplitLaunchEase(t));
                if (t >= 1f) e.IsLaunching = false;
                continue; // the launch fully drives position this tick - no normal movement too
            }

            Vector2 dir;
            if (e is PlayerEntity p)
            {
                var target = _groupTargets.TryGetValue(p.GroupId, out var t2) ? t2 : p.Position;
                dir = SafeDir(target - p.Position);
            }
            else if (e is AiEntity a)
            {
                dir = a.MoveDirection;
            }
            else
            {
                dir = Vector2.Zero;
            }

            Vector2 vel = Vector2.Zero;
            if (dir != Vector2.Zero)
            {
                if (dir.LengthSquared() > 1f) dir = Vector2.Normalize(dir);
                float speed = Rules.MovementSpeedForScale(e.Scale);
                vel = dir * speed;
            }

            if (e.SplitVelocity != Vector2.Zero)
            {
                vel += e.SplitVelocity;
                e.SplitVelocity *= Math.Max(0f, 1f - Rules.SplitVelocityDecayPerSecond * dt);
                if (e.SplitVelocity.LengthSquared() < 0.25f) e.SplitVelocity = Vector2.Zero;
            }

            e.Position += vel * dt;

            e.Position = new Vector2(
                Math.Clamp(e.Position.X, -HalfMapSize, HalfMapSize),
                Math.Clamp(e.Position.Y, -HalfMapSize, HalfMapSize));
        }
    }

    /// <summary>Split siblings that aren't merge-eligible yet get pushed apart by a fractional
    /// position correction every tick, proportional to how deep they overlap - this runs
    /// unconditionally AFTER movement, so unlike a velocity-based spring it can never be outraced
    /// by a piece's own movement speed (e.g. both pieces steering toward the same group target
    /// point at full speed would otherwise overpower a capped repulsion velocity and the pair
    /// would never actually separate). Correcting only a fraction each tick (not the full overlap
    /// at once) still gives a smooth, gradual "settle apart" feel rather than an instant snap.
    /// Eligible pairs are skipped so ResolveMerges can recombine them instead of fighting this.</summary>
    private void ResolveSplitSeparation(float dt)
    {
        var now = DateTime.UtcNow;
        float correctionFraction = Math.Clamp(dt * Rules.SplitSeparationCorrectionRate, 0f, 1f);

        foreach (var group in _players.Values.GroupBy(p => p.GroupId))
        {
            var pieces = group.ToList();
            if (pieces.Count < 2) continue;

            for (int i = 0; i < pieces.Count; i++)
            {
                var a = pieces[i];
                if (a.IsLaunching) continue; // the launch lerp is already authoritative this tick
                for (int j = i + 1; j < pieces.Count; j++)
                {
                    var b = pieces[j];
                    if (b.IsLaunching) continue;
                    if (now >= a.MergeEligibleUtc && now >= b.MergeEligibleUtc) continue;

                    float targetGap = (a.Scale + b.Scale) / 2f + Rules.SplitSeparationPadding;
                    Vector2 delta = a.Position - b.Position;
                    float dist = delta.Length();
                    if (dist >= targetGap) continue;

                    Vector2 dir = dist > 0.0001f ? delta / dist : new Vector2(1f, 0f);
                    float overlap = targetGap - dist;
                    Vector2 correction = dir * overlap * correctionFraction;

                    a.Position = ClampToMap(a.Position + correction / 2f);
                    b.Position = ClampToMap(b.Position - correction / 2f);
                }
            }
        }
    }

    private Vector2 ClampToMap(Vector2 pos) => new(
        Math.Clamp(pos.X, -HalfMapSize, HalfMapSize),
        Math.Clamp(pos.Y, -HalfMapSize, HalfMapSize));

    /// <summary>A cell strictly bigger than a saw that touches it gets forced-split into a few
    /// unevenly-sized pieces (players and bots alike, unlike the virus which only affects
    /// players); anything at or under saw scale just passes through unaffected. A per-entity
    /// cooldown stops the freshly-created pieces from immediately re-popping on the same saw.</summary>
    private void ResolveSawCollisions()
    {
        var now = DateTime.UtcNow;
        var cooldown = TimeSpan.FromSeconds(Rules.SawPopCooldownSeconds);

        foreach (var saw in _saws.Values)
        {
            bool popped = false;

            foreach (var piece in _players.Values)
            {
                if (piece.Scale <= Rules.SawScale) continue;
                if (now - piece.LastSawHitUtc < cooldown) continue;
                float dist = Vector2.Distance(piece.Position, saw.Position);
                if (dist > (piece.Scale + saw.Scale) / 2f) continue;

                PopSawOn(piece, now);
                popped = true;
                break; // mutates _players - must stop enumerating it immediately
            }

            if (!popped)
            {
                foreach (var bot in _bots.Values)
                {
                    if (bot.Scale <= Rules.SawScale) continue;
                    if (now - bot.LastSawHitUtc < cooldown) continue;
                    float dist = Vector2.Distance(bot.Position, saw.Position);
                    if (dist > (bot.Scale + saw.Scale) / 2f) continue;

                    PopSawOnBot(bot, now);
                    break; // mutates _bots - must stop enumerating it immediately
                }
            }
        }
    }

    /// <summary>Unlike the virus's equal-mass pop, weights are randomized so pieces come out
    /// noticeably uneven - some big, some small.</summary>
    private void PopSawOn(PlayerEntity piece, DateTime now)
    {
        int currentPieces = _players.Values.Count(p => p.GroupId == piece.GroupId);
        int freeSlots = Rules.MaxPiecesPerPlayer - currentPieces;
        if (freeSlots <= 0) return;

        int totalPieces = _rng.Next(Rules.SawSplitPiecesMin, Rules.SawSplitPiecesMax + 1);
        int piecesToMake = Math.Min(freeSlots, totalPieces - 1);
        if (piecesToMake <= 0) return;

        var weights = new float[piecesToMake + 1];
        float weightSum = 0f;
        for (int i = 0; i < weights.Length; i++)
        {
            weights[i] = 0.3f + (float)_rng.NextDouble();
            weightSum += weights[i];
        }

        float totalMass = piece.Mass;
        piece.Mass = Math.Max(Rules.MassMin, totalMass * weights[0] / weightSum);
        piece.LastSawHitUtc = now;

        var mergeAt = now.AddSeconds(Rules.MergeCooldownSeconds);
        for (int i = 0; i < piecesToMake; i++)
        {
            float angle = (float)(i + 1) / (piecesToMake + 1) * MathF.PI * 2f;
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            float eachMass = Math.Max(Rules.MassMin, totalMass * weights[i + 1] / weightSum);

            var clone = new PlayerEntity
            {
                Id = _nextId++,
                GroupId = piece.GroupId,
                Position = piece.Position,
                Mass = eachMass,
                Color = piece.Color,
                Name = piece.Name,
                EndPoint = piece.EndPoint,
                InputDirection = piece.InputDirection,
                LastSeenUtc = piece.LastSeenUtc,
                MergeEligibleUtc = mergeAt,
                LastSawHitUtc = now,
            };
            StartLaunch(clone, dir);
            _players[clone.Id] = clone;
        }
        piece.MergeEligibleUtc = mergeAt;
    }

    /// <summary>Bots don't have the player group/merge system - a popped bot just becomes several
    /// smaller, unevenly-sized independent bots, capped like the virus's bot-pop.</summary>
    private void PopSawOnBot(AiEntity bot, DateTime now)
    {
        int botCap = BotCount * 3;
        if (_bots.Count >= botCap) return;

        int totalPieces = _rng.Next(Rules.SawSplitPiecesMin, Rules.SawSplitPiecesMax + 1);
        int piecesToMake = Math.Min(botCap - _bots.Count, totalPieces - 1);
        if (piecesToMake <= 0) return;

        var weights = new float[piecesToMake + 1];
        float weightSum = 0f;
        for (int i = 0; i < weights.Length; i++)
        {
            weights[i] = 0.3f + (float)_rng.NextDouble();
            weightSum += weights[i];
        }

        float totalMass = bot.Mass;
        bot.Mass = Math.Max(Rules.MassMin, totalMass * weights[0] / weightSum);
        bot.LastSawHitUtc = now;
        uint batchId = bot.PopBatchId != 0 ? bot.PopBatchId : _nextId++;
        bot.PopBatchId = batchId;

        for (int i = 0; i < piecesToMake; i++)
        {
            float angle = (float)(i + 1) / (piecesToMake + 1) * MathF.PI * 2f;
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            float eachMass = Math.Max(Rules.MassMin, totalMass * weights[i + 1] / weightSum);

            var clone = new AiEntity
            {
                Id = _nextId++,
                PopBatchId = batchId,
                Position = bot.Position,
                Mass = eachMass,
                Color = bot.Color,
                Name = bot.Name,
                RoamTarget = RandomPosition(),
                LastSawHitUtc = now,
            };
            StartLaunch(clone, dir);
            _bots[clone.Id] = clone;
        }
    }

    /// <summary>An ejected pellet (EjectDirection != zero) that reaches a saw feeds it instead of
    /// respawning as ordinary food. Every FeedThreshold feeds (randomized 2-4, re-rolled after
    /// each trigger), the saw launches a brand new saw a good distance away in the direction that
    /// feed was thrown from - mirrors agar.io's virus-feeding mechanic. Capped so repeated feeding
    /// can't grow the saw population without bound.</summary>
    private void ResolveSawFeeding()
    {
        // Snapshot first: SpawnSawAt below adds to _saws mid-loop, which would otherwise
        // invalidate this enumerator (unlike the pop methods, we don't break - every saw should
        // still get a chance to feed in the same tick).
        foreach (var saw in _saws.Values.ToList())
        {
            FoodItem? fed = null;
            foreach (var food in _food.Values)
            {
                if (food.EjectDirection == Vector2.Zero) continue;
                float dist = Vector2.Distance(food.Position, saw.Position);
                if (dist > saw.Scale / 2f + FoodItem.Radius) continue;
                fed = food;
                break;
            }

            if (fed == null) continue;

            Vector2 launchDir = fed.EjectDirection;
            fed.Position = RandomPosition();
            fed.Velocity = Vector2.Zero;
            fed.EjectDirection = Vector2.Zero;
            _foodChangedThisTick.Add(fed);

            saw.FeedCount++;
            if (saw.FeedCount < saw.FeedThreshold) continue;

            saw.FeedCount = 0;
            saw.FeedThreshold = _rng.Next(Rules.SawFeedThresholdMin, Rules.SawFeedThresholdMax + 1);

            if (_saws.Count >= SawCount * Rules.SawMaxCountMultiplier) continue;
            var dir = launchDir.LengthSquared() > 0.0001f ? Vector2.Normalize(launchDir) : RandomUnitVector();
            SpawnSawAt(saw.Position + dir * Rules.SawFeedLaunchDistance);
        }
    }

    private void MoveFood(float dt)
    {
        foreach (var food in _food.Values)
        {
            if (food.Velocity == Vector2.Zero) continue;

            food.Position += food.Velocity * dt;
            food.Velocity *= Math.Max(0f, 1f - Rules.EjectVelocityDecayPerSecond * dt);
            if (food.Velocity.LengthSquared() < 0.5f) food.Velocity = Vector2.Zero;

            food.Position = new Vector2(
                Math.Clamp(food.Position.X, -HalfMapSize, HalfMapSize),
                Math.Clamp(food.Position.Y, -HalfMapSize, HalfMapSize));

            _foodChangedThisTick.Add(food);
        }
    }

    /// <summary>A cell strictly bigger than the virus that touches it gets forced-split
    /// (agar-style "pop"); anything at or under virus scale just passes through unaffected. Bots
    /// are checked too (not just players) - a virus is a hazard for anything big enough, not a
    /// player-only mechanic.</summary>
    private void ResolveVirusCollisions()
    {
        foreach (var virus in _viruses.Values)
        {
            bool popped = false;

            foreach (var piece in _players.Values)
            {
                if (piece.Scale <= Rules.VirusScale) continue;
                float dist = Vector2.Distance(piece.Position, virus.Position);
                if (dist > (piece.Scale + virus.Scale) / 2f) continue;

                PopVirusOn(piece);
                popped = true;
                break; // mutates _players - must stop enumerating it immediately
            }

            if (!popped)
            {
                foreach (var bot in _bots.Values)
                {
                    if (bot.Scale <= Rules.VirusScale) continue;
                    float dist = Vector2.Distance(bot.Position, virus.Position);
                    if (dist > (bot.Scale + virus.Scale) / 2f) continue;

                    PopVirusOnBot(bot);
                    popped = true;
                    break; // mutates _bots - must stop enumerating it immediately
                }
            }

            if (popped) virus.Position = RandomPosition();
        }
    }

    /// <summary>Bots don't have the player group/merge-eligibility system - a popped bot just
    /// becomes several smaller independent bots (capped so repeated feeding can't runaway-grow
    /// the bot population), which is enough to make the virus an actual threat to them too.</summary>
    private void PopVirusOnBot(AiEntity bot)
    {
        int botCap = BotCount * 3;
        if (_bots.Count >= botCap) return;

        int piecesToMake = Math.Min(botCap - _bots.Count, Rules.VirusBotSplitPieces - 1);
        if (piecesToMake <= 0) return;

        float eachMass = Math.Max(Rules.MassMin, bot.Mass / (piecesToMake + 1));
        bot.Mass = eachMass;
        uint batchId = bot.PopBatchId != 0 ? bot.PopBatchId : _nextId++;
        bot.PopBatchId = batchId;

        for (int i = 0; i < piecesToMake; i++)
        {
            float angle = (float)(i + 1) / (piecesToMake + 1) * MathF.PI * 2f;
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));

            var clone = new AiEntity
            {
                Id = _nextId++,
                PopBatchId = batchId,
                Position = bot.Position,
                Mass = eachMass,
                Color = bot.Color,
                Name = bot.Name,
                RoamTarget = RandomPosition(),
            };
            StartLaunch(clone, dir);
            _bots[clone.Id] = clone;
        }
    }

    private void PopVirusOn(PlayerEntity piece)
    {
        int currentPieces = _players.Values.Count(p => p.GroupId == piece.GroupId);
        int freeSlots = Rules.MaxPiecesPerPlayer - currentPieces;
        if (freeSlots <= 0) return;

        int piecesToMake = Math.Min(freeSlots, Rules.VirusSplitPieces - 1);
        if (piecesToMake <= 0) return;

        float eachMass = Math.Max(Rules.MassMin, piece.Mass / (piecesToMake + 1));
        piece.Mass = eachMass;

        for (int i = 0; i < piecesToMake; i++)
        {
            float angle = (float)(i + 1) / (piecesToMake + 1) * MathF.PI * 2f;
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));

            var mergeAt = DateTime.UtcNow.AddSeconds(Rules.MergeCooldownSeconds);
            var clone = new PlayerEntity
            {
                Id = _nextId++,
                GroupId = piece.GroupId,
                Position = piece.Position,
                Mass = eachMass,
                Color = piece.Color,
                Name = piece.Name,
                EndPoint = piece.EndPoint,
                InputDirection = piece.InputDirection,
                LastSeenUtc = piece.LastSeenUtc,
                MergeEligibleUtc = mergeAt,
            };
            piece.MergeEligibleUtc = mergeAt;
            StartLaunch(clone, dir);
            _players[clone.Id] = clone;
        }
    }

    /// <summary>Split siblings recombine once both sides' MergeEligibleUtc has passed and they're
    /// touching again - this is what turns "split apart" back into "one blob" over time.</summary>
    private void ResolveMerges()
    {
        var now = DateTime.UtcNow;
        foreach (var group in _players.Values.GroupBy(p => p.GroupId))
        {
            if (group.Count() < 2) continue;
            var pieces = group.ToList();

            for (int i = 0; i < pieces.Count; i++)
            {
                var a = pieces[i];
                if (!_players.ContainsKey(a.Id)) continue;

                for (int j = i + 1; j < pieces.Count; j++)
                {
                    var b = pieces[j];
                    if (!_players.ContainsKey(b.Id)) continue;
                    if (now < a.MergeEligibleUtc || now < b.MergeEligibleUtc) continue;

                    float dist = Vector2.Distance(a.Position, b.Position);
                    if (dist > (a.Scale + b.Scale) / 2f) continue;

                    // The group's primary piece (Id == GroupId) is the one the client's session
                    // tracks as "myEntityId" - it must survive any merge it's part of, or the
                    // client silently loses control forever (no protocol message exists to tell
                    // it its id changed). Which of a/b is primary can't depend on which index
                    // happened to land first in this tick's pieces list. Ties (neither piece is
                    // primary, i.e. both are split clones) fall back to the lower Id so the
                    // outcome is deterministic instead of depending on Dictionary enumeration
                    // order.
                    bool aPrimary = a.Id == a.GroupId;
                    bool bPrimary = b.Id == b.GroupId;
                    PlayerEntity survivor, absorbed;
                    if (aPrimary) { survivor = a; absorbed = b; }
                    else if (bPrimary) { survivor = b; absorbed = a; }
                    else if (a.Id < b.Id) { survivor = a; absorbed = b; }
                    else { survivor = b; absorbed = a; }

                    survivor.Mass = Rules.ClampMass(survivor.Mass + absorbed.Mass);
                    _players.Remove(absorbed.Id);

                    if (absorbed.Id == a.Id) break; // a is gone - nothing left for it to absorb this pass
                }
            }
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
                eaten.Velocity = Vector2.Zero;
                eaten.EjectDirection = Vector2.Zero;
                _foodChangedThisTick.Add(eaten);
            }
        }
    }

    /// <summary>Matches real agar.io: eat-eligibility is purely mass-based (Rules.CanEat), not
    /// distance-based. If neither side can ever eat the other at their current masses, they
    /// physically block each other like solid circles instead of overlapping/passing through
    /// ("equal-mass collisions don't eat, they just block") - otherwise the existing eat-distance
    /// gate is unchanged from before.</summary>
    private void ResolveBlobEating()
    {
        var all = AllBlobs().ToList();
        for (int i = 0; i < all.Count; i++)
        {
            var a = all[i];
            for (int j = i + 1; j < all.Count; j++)
            {
                var b = all[j];

                // Split siblings never devour/block each other - ResolveSplitSeparation and
                // ResolveMerges own that relationship instead.
                if (a is PlayerEntity pa && b is PlayerEntity pb && pa.GroupId == pb.GroupId) continue;

                // Virus/saw-pop bot siblings likewise shouldn't immediately cannibalize each
                // other - bots have no merge system to recombine them, so this immunity is
                // permanent for that batch rather than time-limited.
                if (a is AiEntity aa && b is AiEntity ab && aa.PopBatchId != 0 && aa.PopBatchId == ab.PopBatchId) continue;

                bool aEatsB = Rules.CanEat(a.Scale, b.Scale);
                bool bEatsA = Rules.CanEat(b.Scale, a.Scale);

                if (aEatsB || bEatsA)
                {
                    float eatDist = Vector2.Distance(a.Position, b.Position);
                    if (eatDist > Math.Max(a.Scale, b.Scale) / 2f) continue;
                    if (aEatsB) Devour(a, b); else Devour(b, a);
                    continue;
                }

                float touchDist = (a.Scale + b.Scale) / 2f;
                float dist = Vector2.Distance(a.Position, b.Position);
                if (dist >= touchDist) continue;

                PushApart(a, b, touchDist, dist);
            }
        }
    }

    private void PushApart(Entity a, Entity b, float targetGap, float dist)
    {
        Vector2 delta = a.Position - b.Position;
        Vector2 dir = dist > 0.0001f ? delta / dist : new Vector2(1f, 0f);
        float overlap = targetGap - dist;
        a.Position = ClampToMap(a.Position + dir * (overlap / 2f));
        b.Position = ClampToMap(b.Position - dir * (overlap / 2f));
    }

    private void Devour(Entity winner, Entity loser)
    {
        winner.Mass = Rules.ClampMass(winner.Mass + loser.Mass);

        // A losing split piece is just removed if its group still has other pieces alive -
        // "respawn in place" only makes sense for the group's very last remaining piece (an
        // actual player death), otherwise the eaten player would get a free new cell for free.
        if (loser is PlayerEntity lp && _players.Values.Any(p => p.GroupId == lp.GroupId && p.Id != lp.Id))
        {
            _players.Remove(lp.Id);
            return;
        }

        RespawnAsNew(loser);
    }

    private void RespawnAsNew(Entity e)
    {
        e.Mass = Rules.MassMin;
        e.Position = RandomPosition();
        e.SplitVelocity = Vector2.Zero;
        if (e is AiEntity ai)
        {
            ai.State = AiState.Roaming;
            ai.TargetId = null;
            ai.ThreatId = null;
            ai.RoamTarget = RandomPosition();
        }
        else if (e is PlayerEntity lp)
        {
            lp.GroupId = lp.Id;
            lp.MergeEligibleUtc = DateTime.MinValue;
            _groupTargets[lp.GroupId] = lp.Position;
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

    /// <summary>Distinct players (grouped by GroupId), not raw cell/piece count - a split player
    /// must still only count once against room capacity.</summary>
    public int DistinctPlayerCount
    {
        get { lock (_gate) { return _players.Values.Select(p => p.GroupId).Distinct().Count(); } }
    }

    public IReadOnlyCollection<AiEntity> Bots
    {
        get { lock (_gate) { return _bots.Values.ToArray(); } }
    }

    public IReadOnlyCollection<VirusEntity> Viruses
    {
        get { lock (_gate) { return _viruses.Values.ToArray(); } }
    }

    public IReadOnlyCollection<SawEntity> Saws
    {
        get { lock (_gate) { return _saws.Values.ToArray(); } }
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

    /// <summary>Split pieces are summed under one leaderboard entry per group (agar-style total
    /// mass), not listed as separate players.</summary>
    public List<(string Name, float Mass)> GetLeaderboard(int top = 5)
    {
        lock (_gate)
        {
            var playerTotals = _players.Values
                .GroupBy(p => p.GroupId)
                .Select(g => (Name: g.First().Name, Mass: g.Sum(p => p.Mass)));
            var botTotals = _bots.Values.Select(b => (b.Name, b.Mass));

            return playerTotals.Concat(botTotals)
                .OrderByDescending(e => e.Mass)
                .Take(top)
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
