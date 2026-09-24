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
    // Same items as _food, indexable: thrown pellets recycle an existing item (AcquirePelletItem)
    // instead of adding new ones, so the food count - which every client is sent in full on join
    // and never told to shrink - stays exactly FoodCount forever.
    private readonly List<FoodItem> _foodList = new();
    private readonly List<FoodItem> _foodChangedThisTick = new();

    private readonly Dictionary<uint, DateTime> _lastSplitUtc = new();
    private readonly Dictionary<uint, DateTime> _lastEjectUtc = new();
    private readonly Dictionary<uint, DateTime> _lastEmojiUtc = new();

    // Per-group steering point, rebuilt every tick (RefreshGroupCursors) only for groups whose
    // joystick is held. Every piece steers toward it so pieces gather while moving (agar.io-style),
    // but it's derived from where the pieces ARE right now instead of being an integrated position
    // of its own - a persistent cursor kept running away from slow (big) blobs, so they kept
    // walking after the joystick was released and jittered around it once they arrived.
    private readonly Dictionary<uint, Vector2> _groupCursors = new();

    private uint _nextId = 1;

    // Random spawns keep this far from the walls so nothing is born half outside / pinned on them.
    private const float EdgeMargin = 5f;

    public MapSize Size { get; }
    /// <summary>Half the map's extent on each axis (the map is centred on the origin).</summary>
    public float HalfWidth { get; }
    public float HalfHeight { get; }
    /// <summary>Larger of the two - kept for /stats and any square-only consumer.</summary>
    public int HalfMapSize => (int)MathF.Max(HalfWidth, HalfHeight);
    public int BotCount { get; }
    public int FoodCount { get; }
    public int VirusCount { get; }
    public int SawCount { get; }
    public int PlayerCapacity { get; }

    public GameWorld(MapSize mapSize)
    {
        var (width, height) = MapSizes.Dimensions(mapSize);
        Size = mapSize;
        HalfWidth = width / 2f;
        HalfHeight = height / 2f;

        // Everything that scales with the arena keys off the equivalent square's side, so a
        // non-square MapWidth x MapHeight gets the same density as a square of the same area.
        float side = MathF.Sqrt(width * height);
        BotCount = Math.Max(1, (int)(side / 60f));
        FoodCount = Math.Clamp((int)(side * 7f - 1000f), 50, GameConfig.Current.MaxFoodCount);
        VirusCount = Math.Max(3, (int)(side / 150f));
        SawCount = Math.Max(2, (int)(side / 200f));
        PlayerCapacity = Math.Max(2, (int)(side / 20f));
    }

    public void Initialize()
    {
        lock (_gate)
        {
            for (int i = 0; i < FoodCount; i++)
            {
                var food = new FoodItem { Id = _nextId++, Position = RandomPosition() };
                _food[food.Id] = food;
                _foodList.Add(food);
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
            Position = ClampToMap(position, Rules.SawScale / 2f),
            Mass = Rules.SawMass,
            Color = Rules.SawColor,
            Name = "Saw",
            FeedThreshold = _rng.Next(Rules.SawFeedThresholdMin, Rules.SawFeedThresholdMax + 1),
        };
        _saws[saw.Id] = saw;
    }

    private Vector2 RandomPosition() => RandomPoint(_rng);

    /// <summary>A uniformly random point inside the map (minus a small wall margin).</summary>
    public Vector2 RandomPoint(Random rng)
    {
        float x = (float)(rng.NextDouble() * 2 - 1) * MathF.Max(0f, HalfWidth - EdgeMargin);
        float y = (float)(rng.NextDouble() * 2 - 1) * MathF.Max(0f, HalfHeight - EdgeMargin);
        return new Vector2(x, y);
    }

    /// <summary>Keeps a circle of the given radius entirely inside the map: pass the entity's
    /// radius (Scale / 2) so its whole body - not just its centre - stays in bounds and it can
    /// never sit half through a wall.</summary>
    private Vector2 ClampToMap(Vector2 pos, float radius = 0f)
    {
        float maxX = MathF.Max(0f, HalfWidth - radius);
        float maxY = MathF.Max(0f, HalfHeight - radius);
        return new Vector2(Math.Clamp(pos.X, -maxX, maxX), Math.Clamp(pos.Y, -maxY, maxY));
    }

    /// <summary>Best of a handful of random spots: the one farthest from anything that could eat a
    /// fresh MassMin blob. Plain RandomPosition() sometimes dropped a new/respawned cell right on
    /// top of a bigger one, eaten before the player could even react.</summary>
    private Vector2 SafeSpawnPosition(Entity? exclude = null)
    {
        float mine = Rules.CalculateScale(Rules.MassMin);
        var threats = ActiveBlobs().Where(b => b != exclude && Rules.CanEat(b.Scale, mine)).ToList();

        var best = RandomPosition();
        float bestClearance = float.MinValue;
        for (int i = 0; i < Rules.SpawnCandidates; i++)
        {
            var candidate = i == 0 ? best : RandomPosition();
            float clearance = float.MaxValue;
            foreach (var t in threats)
            {
                clearance = MathF.Min(clearance, Vector2.Distance(candidate, t.Position) - t.Scale / 2f);
            }

            if (clearance > bestClearance)
            {
                bestClearance = clearance;
                best = candidate;
            }
            if (bestClearance >= Rules.SpawnSafeClearance) break;
        }
        return best;
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

    // Names are rendered as-is by every client's TextMeshPro labels (rich-text enabled), so a
    // raw "<color=red>...</color>"-style name could reformat/spoof another player's on-screen UI.
    // Stripped here, once, at the only place a name enters the world - every client (present and
    // future) inherits the fix for free instead of each one re-escaping on the way in.
    private const int MaxNameLength = 20;

    private static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unnamed";
        var stripped = name.Replace("<", "").Replace(">", "").Trim();
        if (stripped.Length > MaxNameLength) stripped = stripped[..MaxNameLength];
        return string.IsNullOrWhiteSpace(stripped) ? "Unnamed" : stripped;
    }

    public PlayerEntity AddPlayer(string name, IPEndPoint endPoint)
    {
        lock (_gate)
        {
            var player = new PlayerEntity
            {
                Id = _nextId++,
                Position = SafeSpawnPosition(),
                Mass = Rules.MassMin,
                Color = Rgba.Random(_rng),
                Name = SanitizeName(name),
                EndPoint = endPoint,
            };
            player.GroupId = player.Id;
            _players[player.Id] = player;
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
            _lastEmojiUtc.Remove(id);
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
        // Straight off the wire: a NaN/Infinity here would normalize into NaN, poison the
        // piece's Position, and get broadcast to (and stick in) every client in the room.
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y)) return;
        direction = Vector2.Clamp(direction, -Vector2.One, Vector2.One);
        if (direction.LengthSquared() > 1f) direction = Vector2.Normalize(direction);

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

            var toSplit = pieces.Where(p => p.AbsorbInto == null && p.Mass >= Rules.SplitMinMass).ToList();
            if (toSplit.Count == 0) return;

            // One merge time per split action: everything this press creates becomes merge-eligible together.
            var mergeAt = now + RollMergeTime();
            foreach (var piece in toSplit)
            {
                if (pieceCount >= Rules.MaxPiecesPerPlayer) break;
                SpawnSplitPiece(piece, mergeAt);
                pieceCount++;
            }

            _lastSplitUtc[groupId] = now;
        }
    }

    /// <summary>Picks this split's merge cooldown uniformly from GameConfig's [MergeTimeMin, MergeTimeMax].</summary>
    private TimeSpan RollMergeTime()
    {
        var cfg = GameConfig.Current;
        return TimeSpan.FromSeconds(cfg.MergeTimeMin + (cfg.MergeTimeMax - cfg.MergeTimeMin) * _rng.NextDouble());
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
            foreach (var piece in _players.Values.Where(p => p.GroupId == groupId && p.AbsorbInto == null))
            {
                if (piece.Mass < Rules.EjectMinMass) continue;

                var dir = piece.InputDirection != Vector2.Zero ? SafeDir(piece.InputDirection) : new Vector2(1f, 0f);
                piece.Mass = Rules.ClampMass(piece.Mass - Rules.EjectMassAmount);

                // Only these pellets can feed a saw (EjectDirection set); spike-thrown ones can't.
                LaunchPellet(piece.Position + dir * (piece.Scale / 2f + FoodItem.Radius), dir, Rules.EjectSpeed, feedsSaws: true);
                ejectedAny = true;
            }

            if (ejectedAny) _lastEjectUtc[groupId] = now;
        }
    }

    /// <summary>Emoji had no server-side throttle at all - unlike Split/Eject, a flood of Emoji
    /// packets costs nothing to validate but gets broadcast to every session in the room, so a
    /// spamming client could burden everyone else's bandwidth. Same cooldown pattern as
    /// Split/Eject.</summary>
    public bool TryEmojiCooldown(uint entityId)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_lastEmojiUtc.TryGetValue(entityId, out var last) && now - last < Rules.EmojiCooldown) return false;
            _lastEmojiUtc[entityId] = now;
            return true;
        }
    }

    /// <summary>The one way a new piece of a player is created (manual split, virus pop, saw pop):
    /// same identity/owner as its source, launched away from it along <paramref name="dir"/>.</summary>
    private PlayerEntity CreateClone(PlayerEntity source, float mass, DateTime mergeAt, Vector2 dir)
    {
        var clone = new PlayerEntity
        {
            Id = _nextId++,
            GroupId = source.GroupId,
            Position = source.Position,
            Mass = mass,
            Color = source.Color,
            Name = source.Name,
            EndPoint = source.EndPoint,
            InputDirection = source.InputDirection,
            LastSeenUtc = source.LastSeenUtc,
            MergeEligibleUtc = mergeAt,
            LastSawHitUtc = source.LastSawHitUtc,
        };
        StartLaunch(clone, dir, source);
        _players[clone.Id] = clone;
        return clone;
    }

    private void SpawnSplitPiece(PlayerEntity piece, DateTime mergeAt)
    {
        var dir = piece.InputDirection != Vector2.Zero ? SafeDir(piece.InputDirection) : new Vector2(1f, 0f);
        float newMass = Math.Max(Rules.MassMin, piece.Mass / 2f);
        piece.Mass = newMass;
        piece.MergeEligibleUtc = mergeAt;

        // Only the new clone launches - piece (the group's original entity, which for the primary
        // piece is the one the client's camera follows) stays exactly where it was. Spike pops
        // work the same way, so no split path yanks the source piece.
        CreateClone(piece, newMass, mergeAt, dir);
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

            RefreshGroupCursors();
            MoveEntity(_players.Values, dt);
            foreach (var bot in _bots.Values)
            {
                AiBrain.Think(bot, this, _rng, dt);
            }
            MoveEntity(_bots.Values, dt);

            MoveFood(dt);
            ResolveSawFeeding();
            ResolveHazardCollisions();
            ResolveFoodEating();
            ResolveBlobEating(dt);
            // Last of the position fixes: blocking another player's blob (above) can shove a piece into
            // its own sibling, and the broadcast state must have no sibling overlap left in it.
            ResolveSplitSeparation(dt);
            ResolveMerges();
            AdvanceAbsorptions(dt);
            ApplyMassDecay(dt);
            ConfineToMap();
        }
    }

    /// <summary>Final guarantee that nothing is ever broadcast outside the walls: eating grows a
    /// blob's radius AFTER it was positioned for this tick, which could leave it a hair over the edge.</summary>
    private void ConfineToMap()
    {
        foreach (var e in ActiveBlobs())
        {
            e.Position = ClampToMap(e.Position, e.Scale / 2f);
        }
    }

    /// <summary>Big cells slowly shrink back toward Rules.MassDecayFloor (agar-style), so a leader
    /// has to keep eating to stay the leader instead of only ever growing. Applies to every
    /// piece/bot, and never pushes a cell below the floor.</summary>
    private void ApplyMassDecay(float dt)
    {
        foreach (var e in ActiveBlobs())
        {
            e.Mass = Rules.DecayedMass(e.Mass, dt);
        }
    }

    /// <summary>Puts each held-joystick group's steering point CursorLeadDistance past its
    /// farthest-flung piece, in the input direction (every piece shares the same InputDirection,
    /// see SetPlayerInput). The lead grows with the group's spread so even a piece already ahead
    /// of the centroid still has the point in front of it (never steers backwards), while pieces
    /// off to the side angle inward and gather. Groups with no input get no cursor and simply
    /// stay where they are (see MergeGatherDir).</summary>
    private void RefreshGroupCursors()
    {
        _groupCursors.Clear();
        foreach (var group in _players.Values.Where(p => p.AbsorbInto == null).GroupBy(p => p.GroupId))
        {
            var dir = SafeDir(group.First().InputDirection);
            if (dir == Vector2.Zero) continue;

            float totalMass = 0f;
            var weighted = Vector2.Zero;
            foreach (var p in group)
            {
                totalMass += p.Mass;
                weighted += p.Position * p.Mass;
            }
            var centroid = weighted / totalMass;

            float reach = 0f;
            foreach (var p in group) reach = MathF.Max(reach, Vector2.Distance(p.Position, centroid));

            _groupCursors[group.Key] = centroid + dir * (reach + Rules.CursorLeadDistance);
        }
    }

    /// <summary>With the joystick released, a piece stays put - except merge-eligible ones, which
    /// drift toward the nearest other eligible sibling so a split player actually recombines
    /// while standing still. They merge the moment they touch (ResolveMerges), so there's no
    /// arrival point to overshoot or jitter around.</summary>
    private Vector2 MergeGatherDir(PlayerEntity p, DateTime now)
    {
        if (now < p.MergeEligibleUtc) return Vector2.Zero;

        PlayerEntity? nearest = null;
        float best = float.MaxValue;
        foreach (var o in _players.Values)
        {
            if (o.GroupId != p.GroupId || o.Id == p.Id || o.AbsorbInto != null || now < o.MergeEligibleUtc) continue;
            float d = Vector2.DistanceSquared(p.Position, o.Position);
            if (d < best) { best = d; nearest = o; }
        }
        return nearest == null ? Vector2.Zero : SafeDir(nearest.Position - p.Position);
    }

    /// <summary>Starts the "just been split/popped" launch: a smoothstep displacement (see
    /// Entity.Launch*). Distance scales with the launched piece's size; GameConfig's SplitForce
    /// scales distance and peak speed together, so the throw's duration (derived from them, and
    /// clamped) stays put while its strength changes.</summary>
    private void StartLaunch(Entity e, Vector2 dir, Entity source)
    {
        var cfg = GameConfig.Current;
        float distance = (cfg.SplitDistance + cfg.SplitDistancePerScale * e.Scale) * cfg.SplitForce;
        float peakSpeed = cfg.SplitSpeed * cfg.SplitForce;

        e.LaunchDir = dir;
        e.LaunchDistance = distance;
        // Smoothstep's peak velocity is 1.5x its average, so the whole throw lasts 1.5 * d / peak.
        e.LaunchDuration = Math.Clamp(1.5f * distance / peakSpeed, Rules.SplitLaunchMinSeconds, Rules.SplitLaunchMaxSeconds);
        e.LaunchElapsed = 0f;
        e.LaunchSource = source;
        e.IsLaunching = true;
    }

    private void MoveEntity<T>(IEnumerable<T> entities, float dt) where T : Entity
    {
        var now = DateTime.UtcNow;
        foreach (var e in entities)
        {
            if (e is PlayerEntity { AbsorbInto: not null }) continue; // glides via AdvanceAbsorptions

            Vector2 dir;
            if (e is PlayerEntity p)
            {
                dir = _groupCursors.TryGetValue(p.GroupId, out var cursor)
                    ? SafeDir(cursor - p.Position)
                    : MergeGatherDir(p, now);
            }
            else if (e is AiEntity a)
            {
                dir = a.MoveDirection;
            }
            else
            {
                dir = Vector2.Zero;
            }

            Vector2 delta = Vector2.Zero;
            if (dir != Vector2.Zero)
            {
                if (dir.LengthSquared() > 1f) dir = Vector2.Normalize(dir);
                delta = dir * Rules.MovementSpeedForMass(e.Mass) * dt;
            }

            // The launch is added ON TOP of steering (not instead of it): a thrown piece stays
            // controllable, and when the launch ends there's no speed jump - it just stops
            // contributing, having already eased to zero velocity.
            if (e.IsLaunching)
            {
                float before = Rules.Smoothstep(e.LaunchElapsed / e.LaunchDuration);
                e.LaunchElapsed += dt;
                float after = Rules.Smoothstep(e.LaunchElapsed / e.LaunchDuration);
                delta += e.LaunchDir * e.LaunchDistance * (after - before);

                if (e.LaunchElapsed >= e.LaunchDuration)
                {
                    e.IsLaunching = false;
                    e.LaunchSource = null;
                }
            }

            e.Position = ClampToMap(e.Position + delta, e.Scale / 2f);
        }
    }

    /// <summary>Split siblings that aren't merge-eligible yet behave like solid circles. Overlap is
    /// resolved by sliding them apart, up to SplitSeparationMaxSpeed: quick enough that steering
    /// (both pieces chasing the same cursor) can never push a pair into each other, but capped so
    /// a piece that finishes a launch inside another slides out instead of teleporting. The heavier
    /// piece yields less (so the primary piece the camera follows isn't shoved around by small
    /// clones), and a few passes settle chains of 3+ pieces. Pairs where either is mid-launch
    /// against its own source (or both are mid-launch) are left alone - the launch already moves
    /// them apart. Eligible pairs are skipped so ResolveMerges can recombine them instead of
    /// fighting this. A piece pinned against a wall hands whatever it couldn't move to its partner.</summary>
    private void ResolveSplitSeparation(float dt)
    {
        var now = DateTime.UtcNow;
        float maxPushPerPass = Rules.SplitSeparationMaxSpeed * dt / Rules.SplitSeparationPasses;

        foreach (var group in _players.Values.Where(p => p.AbsorbInto == null).GroupBy(p => p.GroupId))
        {
            var pieces = group.ToList();
            if (pieces.Count < 2) continue;

            for (int pass = 0; pass < Rules.SplitSeparationPasses; pass++)
            {
                bool moved = false;
                for (int i = 0; i < pieces.Count; i++)
                {
                    var a = pieces[i];
                    for (int j = i + 1; j < pieces.Count; j++)
                    {
                        var b = pieces[j];
                        if (a.IsLaunching && b.IsLaunching) continue;
                        if ((a.IsLaunching && a.LaunchSource == b) || (b.IsLaunching && b.LaunchSource == a)) continue;
                        if (now >= a.MergeEligibleUtc && now >= b.MergeEligibleUtc) continue;

                        float targetGap = (a.Scale + b.Scale) / 2f + Rules.SplitSeparationPadding;
                        Vector2 delta = a.Position - b.Position;
                        float dist = delta.Length();
                        if (dist >= targetGap) continue;

                        Vector2 dir = dist > 0.0001f ? delta / dist : new Vector2(1f, 0f);
                        float push = MathF.Min(targetGap - dist, maxPushPerPass);
                        float totalMass = a.Mass + b.Mass;

                        Vector2 wantA = a.Position + dir * (push * b.Mass / totalMass);
                        Vector2 newA = ClampToMap(wantA, a.Scale / 2f);
                        Vector2 shortfallA = wantA - newA; // what the wall stopped A from doing
                        Vector2 newB = ClampToMap(b.Position - dir * (push * a.Mass / totalMass) - shortfallA, b.Scale / 2f);

                        a.Position = newA;
                        b.Position = newB;
                        moved = true;
                    }
                }
                if (!moved) break;
            }
        }
    }

    // ---- Spikes (virus + saw) -------------------------------------------------------------

    /// <summary>A cell strictly bigger than a spike that touches it gets popped (players and
    /// bots alike). Both hazard types share one deterministic rule: Rules.SpikePieceCount(mass)
    /// equal pieces, launched evenly around the spike's outward direction, throwing
    /// GameConfig.SpikyFoodCount pellets. Anything at or under the spike's scale passes through.
    /// A per-entity cooldown stops the freshly-created pieces re-popping on the same spike.</summary>
    private void ResolveHazardCollisions()
    {
        var now = DateTime.UtcNow;
        var cooldown = TimeSpan.FromSeconds(Rules.HazardPopCooldownSeconds);

        foreach (var hazard in _viruses.Values.Cast<Entity>().Concat(_saws.Values))
        {
            bool popped = false;

            foreach (var piece in _players.Values)
            {
                if (piece.AbsorbInto != null || piece.Scale <= hazard.Scale) continue;
                if (now - piece.LastSawHitUtc < cooldown) continue;
                if (Vector2.Distance(piece.Position, hazard.Position) > (piece.Scale + hazard.Scale) / 2f) continue;

                popped = PopHazardOnPlayer(piece, hazard, now);
                break; // mutates _players - must stop enumerating it immediately
            }

            if (!popped)
            {
                foreach (var bot in _bots.Values)
                {
                    if (bot.Scale <= hazard.Scale) continue;
                    if (now - bot.LastSawHitUtc < cooldown) continue;
                    if (Vector2.Distance(bot.Position, hazard.Position) > (bot.Scale + hazard.Scale) / 2f) continue;

                    popped = PopHazardOnBot(bot, hazard, now);
                    break; // mutates _bots - must stop enumerating it immediately
                }
            }

            if (popped && hazard is VirusEntity) hazard.Position = RandomPosition();
        }
    }

    private bool PopHazardOnPlayer(PlayerEntity piece, Entity hazard, DateTime now)
    {
        int currentPieces = _players.Values.Count(p => p.GroupId == piece.GroupId);
        int freeSlots = Rules.MaxPiecesPerPlayer - currentPieces;
        int total = Math.Min(Rules.SpikePieceCount(piece.Mass), freeSlots + 1);
        if (total < 2) return false;

        int pellets = SpikePelletBudget(piece.Mass);
        float each = Math.Max(Rules.MassMin, (piece.Mass - pellets * Rules.FoodMassGain) / total);
        var mergeAt = now + RollMergeTime();

        piece.Mass = each;
        piece.LastSawHitUtc = now;
        piece.MergeEligibleUtc = mergeAt;

        var origins = new List<Entity> { piece };
        foreach (var dir in PopDirections(piece.Position, hazard.Position, total))
        {
            origins.Add(CreateClone(piece, each, mergeAt, dir));
        }

        EmitSpikeFood(origins, pellets);
        return true;
    }

    /// <summary>Bots don't have the player group/merge system - a popped bot just becomes several
    /// smaller independent bots (capped so repeated popping can't runaway-grow the bot
    /// population), which is enough to make spikes an actual threat to them too.</summary>
    private bool PopHazardOnBot(AiEntity bot, Entity hazard, DateTime now)
    {
        int botCap = BotCount * 3;
        int total = Math.Min(Rules.SpikePieceCount(bot.Mass), botCap - _bots.Count + 1);
        if (total < 2) return false;

        int pellets = SpikePelletBudget(bot.Mass);
        float each = Math.Max(Rules.MassMin, (bot.Mass - pellets * Rules.FoodMassGain) / total);
        uint batchId = bot.PopBatchId != 0 ? bot.PopBatchId : _nextId++;

        bot.Mass = each;
        bot.LastSawHitUtc = now;
        bot.PopBatchId = batchId;

        var origins = new List<Entity> { bot };
        foreach (var dir in PopDirections(bot.Position, hazard.Position, total))
        {
            var clone = new AiEntity
            {
                Id = _nextId++,
                PopBatchId = batchId,
                Position = bot.Position,
                Mass = each,
                Color = bot.Color,
                Name = bot.Name,
                RoamTarget = RandomPosition(),
                LastSawHitUtc = now,
            };
            StartLaunch(clone, dir, bot);
            _bots[clone.Id] = clone;
            origins.Add(clone);
        }

        EmitSpikeFood(origins, pellets);
        return true;
    }

    /// <summary>total-1 launch directions spaced evenly around the circle, starting from the
    /// direction the spike pushes the cell (the source piece "takes" that first slot and stays
    /// put). Fully determined by where the cell hit the spike - no randomness.</summary>
    private static IEnumerable<Vector2> PopDirections(Vector2 cell, Vector2 hazard, int total)
    {
        var outward = SafeDir(cell - hazard);
        if (outward == Vector2.Zero) outward = new Vector2(1f, 0f);
        float baseAngle = MathF.Atan2(outward.Y, outward.X);

        for (int i = 1; i < total; i++)
        {
            float angle = baseAngle + i * MathF.PI * 2f / total;
            yield return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }
    }

    /// <summary>How many pellets a pop throws: SpikyFoodCount, but the cell pays for them out of its
    /// own mass (FoodMassGain each), so never more than 20% of it.</summary>
    private static int SpikePelletBudget(float mass) =>
        Math.Clamp((int)(mass * 0.2f / Rules.FoodMassGain), 0, GameConfig.Current.SpikyFoodCount);

    /// <summary>Throws <paramref name="count"/> pellets out of the pieces a spike just made
    /// (round-robin over them), spread around the circle, each flying 40-100% of
    /// SpikyFoodLaunchDistance before it comes to rest. Ordinary food afterwards - it can be
    /// eaten but doesn't feed saws.</summary>
    private void EmitSpikeFood(IReadOnlyList<Entity> origins, int count)
    {
        float maxDistance = GameConfig.Current.SpikyFoodLaunchDistance;
        float phase = (float)(_rng.NextDouble() * Math.PI * 2);

        for (int k = 0; k < count; k++)
        {
            var origin = origins[k % origins.Count];
            float angle = phase + k * MathF.PI * 2f / count;
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));

            // Pellet velocity decays exponentially (MoveFood), so it travels ~ speed / decay in total.
            float distance = maxDistance * (0.4f + 0.6f * (float)_rng.NextDouble());
            float speed = distance * Rules.EjectVelocityDecayPerSecond;

            LaunchPellet(origin.Position + dir * (origin.Scale / 2f + FoodItem.Radius), dir, speed, feedsSaws: false);
        }
    }

    // ---- Food -------------------------------------------------------------------------------

    /// <summary>Recycles an existing food item as a thrown pellet rather than creating one: the
    /// item that disappears is the idle one farthest from any player (best of a few random
    /// picks), so it vanishes off-screen instead of in front of someone.</summary>
    private FoodItem AcquirePelletItem()
    {
        if (_foodList.Count == 0) // never true for a room (Initialize seeds FoodCount items), but don't crash a bare world
        {
            var fresh = new FoodItem { Id = _nextId++ };
            _food[fresh.Id] = fresh;
            _foodList.Add(fresh);
            return fresh;
        }

        FoodItem? best = null;
        float bestClearance = float.MinValue;

        for (int i = 0; i < 8; i++)
        {
            var candidate = _foodList[_rng.Next(_foodList.Count)];
            if (candidate.Velocity != Vector2.Zero || candidate.EjectDirection != Vector2.Zero) continue;

            float nearest = float.MaxValue;
            foreach (var p in _players.Values) nearest = MathF.Min(nearest, Vector2.DistanceSquared(p.Position, candidate.Position));
            if (nearest > bestClearance)
            {
                bestClearance = nearest;
                best = candidate;
            }
        }

        return best ?? _foodList[_rng.Next(_foodList.Count)];
    }

    private void LaunchPellet(Vector2 from, Vector2 dir, float speed, bool feedsSaws)
    {
        var pellet = AcquirePelletItem();
        pellet.Position = ClampToMap(from);
        pellet.Velocity = dir * speed;
        pellet.EjectDirection = feedsSaws ? dir : Vector2.Zero;
        pellet.EatImmunity = Rules.PelletEatImmunitySeconds;
        _foodChangedThisTick.Add(pellet);
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
            if (food.EatImmunity > 0f) food.EatImmunity -= dt;
            if (food.Velocity == Vector2.Zero) continue;

            food.Position += food.Velocity * dt;
            food.Velocity *= Math.Max(0f, 1f - Rules.EjectVelocityDecayPerSecond * dt);
            if (food.Velocity.LengthSquared() < 0.5f) food.Velocity = Vector2.Zero;

            food.Position = ClampToMap(food.Position);

            _foodChangedThisTick.Add(food);
        }
    }

    // ---- Merging ----------------------------------------------------------------------------

    /// <summary>Split siblings recombine once both sides' MergeEligibleUtc has passed and they're
    /// touching again - this is what turns "split apart" back into "one blob" over time. Touching
    /// only STARTS the merge (BeginAbsorb); AdvanceAbsorptions plays it out over
    /// Rules.MergeAnimSeconds. Each piece takes part in at most one merge at a time.</summary>
    private void ResolveMerges()
    {
        var now = DateTime.UtcNow;

        var busy = new HashSet<PlayerEntity>();
        foreach (var p in _players.Values)
        {
            if (p.AbsorbInto == null) continue;
            busy.Add(p);
            busy.Add(p.AbsorbInto);
        }

        foreach (var group in _players.Values.GroupBy(p => p.GroupId))
        {
            var pieces = group.ToList();
            if (pieces.Count < 2) continue;

            for (int i = 0; i < pieces.Count; i++)
            {
                var a = pieces[i];
                if (busy.Contains(a)) continue;

                for (int j = i + 1; j < pieces.Count; j++)
                {
                    var b = pieces[j];
                    if (busy.Contains(a) || busy.Contains(b)) continue;
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

                    absorbed.AbsorbInto = survivor;
                    absorbed.AbsorbStart = absorbed.Position;
                    absorbed.AbsorbElapsed = 0f;
                    absorbed.AbsorbMassTotal = absorbed.Mass;
                    absorbed.AbsorbMassMoved = 0f;
                    absorbed.IsLaunching = false;
                    busy.Add(a);
                    busy.Add(b);
                }
            }
        }
    }

    /// <summary>Plays out every in-progress merge: the absorbed piece glides (smoothstep) into
    /// the survivor while its mass drains across, then is removed. Total mass is conserved at
    /// every tick. If the survivor disappears mid-way (eaten) the piece simply carries on as a
    /// normal, smaller piece.</summary>
    private void AdvanceAbsorptions(float dt)
    {
        foreach (var piece in _players.Values.Where(p => p.AbsorbInto != null).ToList())
        {
            var target = piece.AbsorbInto!;
            if (!IsAlive(target))
            {
                piece.AbsorbInto = null;
                piece.Mass = Rules.ClampMass(piece.Mass);
                continue;
            }

            piece.AbsorbElapsed += dt;
            float t = Math.Clamp(piece.AbsorbElapsed / Rules.MergeAnimSeconds, 0f, 1f);

            float moved = piece.AbsorbMassTotal * t;
            target.Mass = Rules.ClampMass(target.Mass + (moved - piece.AbsorbMassMoved));
            piece.AbsorbMassMoved = moved;
            piece.Mass = piece.AbsorbMassTotal - moved;

            piece.Position = Vector2.Lerp(piece.AbsorbStart, target.Position, Rules.Smoothstep(t));

            if (t >= 1f) _players.Remove(piece.Id);
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

        foreach (var e in ActiveBlobs())
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
                        if (food.EatImmunity > 0f) continue;
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

    // ---- Eating / blocking ------------------------------------------------------------------

    /// <summary>Matches real agar.io: eat-eligibility is purely mass-based (Rules.CanEat), not
    /// distance-based. If neither side can ever eat the other at their current masses, they
    /// physically block each other like solid circles instead of overlapping/passing through
    /// ("equal-mass collisions don't eat, they just block") - otherwise the existing eat-distance
    /// gate is unchanged from before.</summary>
    private void ResolveBlobEating(float dt)
    {
        var all = ActiveBlobs().ToList();
        for (int i = 0; i < all.Count; i++)
        {
            var a = all[i];
            for (int j = i + 1; j < all.Count; j++)
            {
                var b = all[j];

                // The list was snapshotted up front: something eaten earlier in this pass must not
                // eat (or be eaten) again from beyond the grave.
                if (!IsAlive(a) || !IsAlive(b)) continue;

                // Split siblings never devour/block each other - ResolveSplitSeparation and
                // ResolveMerges own that relationship instead.
                if (a is PlayerEntity pa && b is PlayerEntity pb && pa.GroupId == pb.GroupId) continue;

                // Spike-pop bot siblings likewise shouldn't immediately cannibalize each
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

                PushApart(a, b, touchDist, dist, dt);
            }
        }
    }

    private void PushApart(Entity a, Entity b, float targetGap, float dist, float dt)
    {
        Vector2 delta = a.Position - b.Position;
        Vector2 dir = dist > 0.0001f ? delta / dist : new Vector2(1f, 0f);
        // Capped like split separation: two blobs that end up overlapped slide apart rather than snap.
        float overlap = MathF.Min(targetGap - dist, Rules.SplitSeparationMaxSpeed * dt);
        a.Position = ClampToMap(a.Position + dir * (overlap / 2f), a.Scale / 2f);
        b.Position = ClampToMap(b.Position - dir * (overlap / 2f), b.Scale / 2f);
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
            if (lp.Id == lp.GroupId) PromotePrimary(lp.GroupId);
            return;
        }

        RespawnAsNew(loser);
    }

    /// <summary>The primary piece (Id == GroupId) is the only entity the client's session knows
    /// about: its camera and joystick input are bound to that Id and there's no message to tell it
    /// a different one. When the primary is eaten but other pieces survive, the largest survivor
    /// takes over its Id, so the client keeps control (its own blob just slides to that piece).</summary>
    private void PromotePrimary(uint groupId)
    {
        var heir = _players.Values
            .Where(p => p.GroupId == groupId)
            .OrderByDescending(p => p.AbsorbInto == null)
            .ThenByDescending(p => p.Mass)
            .FirstOrDefault();
        if (heir == null) return;

        _players.Remove(heir.Id);
        heir.Id = groupId;
        heir.AbsorbInto = null;
        _players[groupId] = heir;
    }

    private void RespawnAsNew(Entity e)
    {
        e.Mass = Rules.MassMin;
        e.Position = SafeSpawnPosition(e);
        e.IsLaunching = false;
        e.LaunchSource = null;
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
        }
    }

    private IEnumerable<Entity> AllBlobs()
    {
        foreach (var p in _players.Values) yield return p;
        foreach (var a in _bots.Values) yield return a;
    }

    /// <summary>Every blob that takes part in the game right now - pieces mid-merge are already
    /// "part of" their survivor and don't eat, get eaten, collide, pop or spawn-block.</summary>
    private IEnumerable<Entity> ActiveBlobs() => AllBlobs().Where(e => e is not PlayerEntity { AbsorbInto: not null });

    /// <summary>True while this exact object is still the one registered under its Id (an entity
    /// eaten earlier this tick, or one re-keyed by PromotePrimary, is a different story).</summary>
    private bool IsAlive(Entity e) => e switch
    {
        PlayerEntity p => _players.TryGetValue(p.Id, out var current) && ReferenceEquals(current, p),
        AiEntity a => _bots.TryGetValue(a.Id, out var current) && ReferenceEquals(current, a),
        _ => true,
    };

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
