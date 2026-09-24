namespace CellSimulator.Server.Game;

/// <summary>
/// Formulas ported from the original Unity project (Assets/_Scripts/Game/Utils.cs and
/// PlayerBlob.cs). Kept as plain math so the server can run without Unity. Gameplay-feel numbers
/// that someone might want to tune live in <see cref="GameConfig"/> instead; what's left here are
/// the fixed rules of the game.
/// </summary>
public static class Rules
{
    public const float ScaleMultiplier = 1.15f;

    public const float BlobScaleMin = 2f;

    // GameWorld.ResolveBlobEating gates eating on dist > Max(a.Scale, b.Scale) / 2, i.e. a
    // blob's "reach" grows with its own scale. Without a cap, past a certain scale a blob's reach
    // spans the whole map, so it devours everything every tick and snowballs straight to MassMax
    // in minutes.
    // ponytail: not map-size-aware; if Large/Huge maps get used, derive this from the map size.
    public const float BlobScaleMax = 80f;

    public const float MassMin = 5f;
    public const float MassMax = 1_000_000f;

    public const float FoodMassGain = 1f;

    // Passive shrink for big cells: each tick a cell above MassDecayFloor loses this fraction of
    // its mass per second (never below the floor). Tuning knob for anti-snowball pressure.
    public const float MassDecayFloor = 300f;
    public const float MassDecayPerSecond = 0.0015f;

    // Spawning: try this many random spots and keep the one with the most breathing room from
    // anything that could eat a fresh cell; stop early once a spot clears SpawnSafeClearance.
    public const int SpawnCandidates = 12;
    public const float SpawnSafeClearance = 40f;

    // Split: press-to-split doubles cell count (every eligible cell splits at once, agar-style).
    // How far/fast pieces are thrown and how long until they merge back: see GameConfig.
    public const float SplitMinMass = 32f;
    public const int MaxPiecesPerPlayer = 16;
    private static readonly TimeSpan SplitEjectCooldown = TimeSpan.FromMilliseconds(200);
    public static TimeSpan SplitCooldown => SplitEjectCooldown;
    public static TimeSpan EjectCooldown => SplitEjectCooldown;

    // Matches the client's EMOJI_BUBBLE_DURATION (PlayerBlob.cs) so a new emoji can't interrupt
    // the previous bubble's own display time.
    public static readonly TimeSpan EmojiCooldown = TimeSpan.FromSeconds(1.5);

    // Launch timing bounds: GameConfig gives distance and peak speed, the duration falls out of
    // them (smoothstep peaks at 1.5x the average speed) but is clamped so a tiny/huge throw can't
    // become an instant snap or a slow crawl.
    public const float SplitLaunchMinSeconds = 0.25f;
    public const float SplitLaunchMaxSeconds = 1.0f;

    // Smoothstep: zero velocity at both ends, so a launch neither jerks off nor stops dead.
    public static float Smoothstep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // Absorb ("merge") animation: the absorbed piece glides into the survivor and hands its mass
    // over gradually, instead of being deleted on the tick it touches.
    public const float MergeAnimSeconds = 0.3f;

    // A thrown pellet (W eject or spike) can't be eaten for this long after leaving its thrower.
    public const float PelletEatImmunitySeconds = 0.4f;

    // Eject mass (W-key food throw).
    public const float EjectMinMass = 40f;
    public const float EjectMassAmount = 14f;
    public const float EjectSpeed = 45f;
    public const float EjectVelocityDecayPerSecond = 2f;

    // Spikes: only pop cells strictly bigger than them; too-small cells pass through. How many
    // pieces a pop makes and how much food it throws is GameConfig (SpikySplit*/SpikyFood*).
    public const float VirusScale = 50f;
    public static readonly float VirusMass = VirusScale * VirusScale / ScaleMultiplier;
    public static readonly Rgba VirusColor = new() { R = 60, G = 220, B = 90, A = 255 };

    // A cell that was just popped by a spike ignores spikes for this long, so its brand-new pieces
    // can't immediately re-pop on the same spike.
    public const float HazardPopCooldownSeconds = 1f;

    // Steering: while the joystick is held, a group's pieces steer toward a point this far past
    // its farthest piece in the input direction (GameWorld.RefreshGroupCursors), so they gather
    // as they travel. Released joystick = no steering at all.
    public const float CursorLeadDistance = 15f;

    // Split separation: non-merge-eligible siblings are treated as solid circles. Overlap is
    // resolved by sliding pieces apart at up to SplitSeparationMaxSpeed (spread over
    // SplitSeparationPasses passes per tick) - fast enough that ordinary steering can't push a
    // pair into each other, but capped so a piece that ends a launch inside another slides out
    // instead of teleporting.
    public const float SplitSeparationPadding = 0.5f;
    public const int SplitSeparationPasses = 32;
    public const float SplitSeparationMaxSpeed = 300f;

    // Saw: a smaller, more frequent spike that can also be fed (below). Pops like the virus.
    public const float SawScale = 14f;
    public static readonly float SawMass = SawScale * SawScale / ScaleMultiplier;
    public static readonly Rgba SawColor = new() { R = 40, G = 200, B = 60, A = 255 };

    // Saw feeding: an ejected food pellet that reaches a saw feeds it (consumed, doesn't respawn
    // as normal food). Every SawFeedThresholdMin..Max feeds (randomized per saw, re-rolled after
    // each trigger), the saw launches a brand new saw a good distance away in the direction that
    // feed was thrown from - mirrors agar.io's virus-feeding mechanic.
    public const int SawFeedThresholdMin = 2;
    public const int SawFeedThresholdMax = 4;
    public const float SawFeedLaunchDistance = 55f;
    public const int SawMaxCountMultiplier = 5; // hard cap: GameWorld.SawCount * this

    public static float CalculateScale(float mass)
    {
        float c = MathF.Sqrt(mass * ScaleMultiplier);
        return Math.Clamp(c, BlobScaleMin, BlobScaleMax);
    }

    public static float ClampMass(float mass) => Math.Clamp(mass, MassMin, MassMax);

    public static float DecayedMass(float mass, float dt) =>
        mass <= MassDecayFloor ? mass : Math.Max(MassDecayFloor, mass * (1f - MassDecayPerSecond * dt));

    /// <summary>True if an entity with eaterScale can eat one with preyScale (must be at least ~15% bigger).</summary>
    public static bool CanEat(float eaterScale, float preyScale) => eaterScale > preyScale * ScaleMultiplier;

    /// <summary>Score/mass -> speed (units/sec). Smoothly falls from MaxSpeed as the cell grows -
    /// no cliffs, and clamped to [MinSpeed, MaxSpeed] so huge cells never crawl or fly and tiny ones
    /// are never sluggish. All knobs in GameConfig (MinSpeed/MaxSpeed/ScoreToSpeedMultiplier).</summary>
    public static float MovementSpeedForMass(float mass)
    {
        var cfg = GameConfig.Current;
        float growth = MathF.Max(0f, MathF.Sqrt(mass) - MathF.Sqrt(MassMin));
        return Math.Clamp(cfg.MaxSpeed / (1f + cfg.ScoreToSpeedMultiplier * growth), cfg.MinSpeed, cfg.MaxSpeed);
    }

    /// <summary>Deterministic spike pop size: 2 pieces, plus one more per SpikySplitThreshold of
    /// mass, capped at SpikySplitCount. Same cell mass, same result - no dice.</summary>
    public static int SpikePieceCount(float mass)
    {
        var cfg = GameConfig.Current;
        return Math.Clamp(2 + (int)(mass / cfg.SpikySplitThreshold), 2, cfg.SpikySplitCount);
    }
}

public enum MapSize
{
    Huge,
    Large,
    Medium,
    Small,
}

public static class MapSizes
{
    // The dropdown's four choices are multiples of the configured default (Small) map, keeping
    // the same 1:2:3:5 ratios they always had. Everything else that scales with the arena
    // (food, bots, spikes, capacity) is derived from the resulting size in GameWorld.
    public static float Multiplier(MapSize size) => size switch
    {
        MapSize.Huge => 5f,
        MapSize.Large => 3f,
        MapSize.Medium => 2f,
        _ => 1f,
    };

    public static (float Width, float Height) Dimensions(MapSize size)
    {
        var cfg = GameConfig.Current;
        float m = Multiplier(size);
        return (cfg.MapWidth * m, cfg.MapHeight * m);
    }
}
