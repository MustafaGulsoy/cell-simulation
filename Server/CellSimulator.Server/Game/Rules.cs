namespace CellSimulator.Server.Game;

/// <summary>
/// Formulas ported from the original Unity project (Assets/_Scripts/Game/Utils.cs and
/// PlayerBlob.cs). Kept as plain math so the server can run without Unity.
/// </summary>
public static class Rules
{
    public const float ScaleMultiplier = 1.15f;

    public const float BlobScaleMin = 2f;

    // GameWorld.ResolveBlobEating gates eating on dist > Max(a.Scale, b.Scale) / 2, i.e. a
    // blob's "reach" grows with its own scale. 2000 was sized for a Huge map (half=1000) but
    // the server defaults to Small (half=100, ~141 diagonal): past scale~140 a blob's reach
    // already spans the whole map, so it devours everything every tick and snowballs straight
    // to MassMax in minutes. Capped for the Small map actually in use.
    // ponytail: not map-size-aware; if Large/Huge maps get used, derive this from HalfMapSize.
    public const float BlobScaleMax = 80f;

    public const float MassMin = 5f;
    public const float MassMax = 1_000_000f;

    // Original values (14/244) drove an Impulse applied every FixedUpdate on a Rigidbody2D
    // with LinearDamping=10 (Player/AI prefabs). Impulse/mass cancels mass, so under repeated
    // impulse + damping the terminal speed converges to v* = appliedSpeed / damping, i.e. the
    // old game actually ran at 1.4-24.4 units/sec, not 14-244. We move by direct position
    // integration now (pos += dir * speed * dt), so these are literal units/sec - use the
    // derived terminal speeds directly instead of the raw old force constants.
    public const float MovementSpeedMin = 1.4f;
    public const float MovementSpeedMax = 24.4f;

    public const float FoodMassGain = 1f;

    // Split: press-to-split doubles cell count (every eligible cell splits at once, agar-style).
    public const float SplitMinMass = 32f;
    public const int MaxPiecesPerPlayer = 16;
    public const float SplitVelocityDecayPerSecond = 3f;
    public const float MergeCooldownSeconds = 15f;
    private static readonly TimeSpan SplitEjectCooldown = TimeSpan.FromMilliseconds(200);
    public static TimeSpan SplitCooldown => SplitEjectCooldown;
    public static TimeSpan EjectCooldown => SplitEjectCooldown;

    // Matches the client's EMOJI_BUBBLE_DURATION (PlayerBlob.cs) so a new emoji can't interrupt
    // the previous bubble's own display time.
    public static readonly TimeSpan EmojiCooldown = TimeSpan.FromSeconds(1.5);

    // Split/pop launch animation: a lerp from start to a size-proportional target (bigger cell ->
    // farther AND faster, since distance/duration = speed and duration is fixed), eased so ~90%
    // of the distance covers quickly and the last stretch creeps in - like it caught on something
    // and is pulling itself the rest of the way. GameWorld.StartLaunch/MoveEntity.
    public const float SplitLaunchBaseDistance = 3f;
    public const float SplitLaunchDistancePerScale = 1.2f;
    public const float SplitLaunchDuration = 0.45f;

    public static float SplitLaunchEase(float t) => 1f - MathF.Pow(1f - t, 4f);

    // Eject mass (W-key food throw).
    public const float EjectMinMass = 40f;
    public const float EjectMassAmount = 14f;
    public const float EjectSpeed = 45f;
    public const float EjectVelocityDecayPerSecond = 2f;

    // Virus/explosion hazard: only pops cells strictly bigger than it; too-small cells pass through.
    public const float VirusScale = 50f;
    public static readonly float VirusMass = VirusScale * VirusScale / ScaleMultiplier;
    public const int VirusSplitPieces = 8; // total pieces the popped cell becomes (capped by MaxPiecesPerPlayer)
    public const int VirusBotSplitPieces = 4; // bots don't have the player group/merge system, so fewer, simpler pieces
    public static readonly Rgba VirusColor = new() { R = 60, G = 220, B = 90, A = 255 };

    // Split separation: after the initial launch (see SplitLaunch* below) settles, siblings that
    // drifted back together (e.g. both steering toward the same group target point) would
    // otherwise sit fully overlapped with nothing to keep them apart. GameWorld.ResolveSplitSeparation
    // corrects a FRACTION of the overlap by direct position each tick whenever a pair is closer
    // than this target gap - unconditional and after movement, so it can't be outraced by a
    // piece's own movement speed the way a velocity-based repulsion could - this only runs while
    // the pair isn't merge-eligible yet.
    public const float SplitSeparationPadding = 0.5f;
    public const float SplitSeparationCorrectionRate = 10f; // fraction-of-overlap corrected per second

    // Saw hazard: forces a cell strictly bigger than it to pop into a FEW (2-4) UNEVENLY sized
    // pieces - unlike the virus's even split, some pieces come out noticeably bigger than others.
    // Affects players and bots alike (unlike the virus, which only affects players). A per-entity
    // cooldown stops the freshly-created pieces from immediately re-popping on the same saw.
    public const float SawScale = 14f;
    public static readonly float SawMass = SawScale * SawScale / ScaleMultiplier;
    public const int SawSplitPiecesMin = 2;
    public const int SawSplitPiecesMax = 4;
    public const float SawPopCooldownSeconds = 1f;
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

    /// <summary>True if an entity with eaterScale can eat one with preyScale (must be at least ~15% bigger).</summary>
    public static bool CanEat(float eaterScale, float preyScale) => eaterScale > preyScale * ScaleMultiplier;

    // Old formula (sqrt(5000/((scale+6)*0.01))) only dropped below MovementSpeedMax past
    // scale~834 - unreachable since BlobScaleMax is 80, so every real blob clamped to the same
    // max speed regardless of size and "slows down as it grows" never actually happened. Linear
    // interpolation across the real [BlobScaleMin, BlobScaleMax] range instead.
    public static float MovementSpeedForScale(float scale)
    {
        float t = Math.Clamp((scale - BlobScaleMin) / (BlobScaleMax - BlobScaleMin), 0f, 1f);
        return MovementSpeedMax - (MovementSpeedMax - MovementSpeedMin) * t;
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
    // 3x the previous 1500/900/600/300 set (which was itself +50% over the original shipped
    // 1000/600/400/200). FoodCount/BotCount/VirusCount/SawCount/PlayerCapacity in GameWorld all
    // derive from this side length already, so they scale up automatically - nothing else to tune.
    public static int SideLength(MapSize size) => size switch
    {
        MapSize.Huge => 4500,
        MapSize.Large => 2700,
        MapSize.Medium => 1800,
        MapSize.Small => 900,
        _ => 900,
    };
}
