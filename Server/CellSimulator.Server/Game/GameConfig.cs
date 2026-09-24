namespace CellSimulator.Server.Game;

/// <summary>
/// The one place gameplay feel is tuned. Every value has a default here and can be overridden
/// without recompiling from appsettings.json's "Game" section or an environment variable
/// (e.g. <c>Game__SplitForce=1.3</c>) - Program.cs binds it once at startup. Constants that are
/// really rules (mass formulas, scale limits) stay in <see cref="Rules"/>.
/// </summary>
public sealed class GameConfig
{
    public static GameConfig Current { get; private set; } = new();

    public static void Use(GameConfig config)
    {
        config.Normalize();
        Current = config;
    }

    // ---- Split ----
    /// <summary>Strength of a split/pop launch: scales BOTH how far and how fast pieces are thrown
    /// (so the launch takes the same time, it just goes farther/harder). 1 = as tuned below.</summary>
    public float SplitForce { get; set; } = 1f;
    /// <summary>Base launch distance in world units (before SplitDistancePerScale).</summary>
    public float SplitDistance { get; set; } = 3f;
    /// <summary>Extra launch distance per unit of the launched piece's own scale - bigger cell, farther throw.</summary>
    public float SplitDistancePerScale { get; set; } = 1.2f;
    /// <summary>Peak launch speed in units/second. The launch eases in and out (smoothstep), so this
    /// is reached mid-flight - it never starts or stops abruptly.</summary>
    public float SplitSpeed { get; set; } = 90f;
    /// <summary>A split's merge cooldown is picked uniformly in [MergeTimeMin, MergeTimeMax] seconds.</summary>
    public float MergeTimeMin { get; set; } = 23f;
    public float MergeTimeMax { get; set; } = 26f;

    // ---- Speed (score/mass -> movement speed) ----
    public float MinSpeed { get; set; } = 3f;
    public float MaxSpeed { get; set; } = 24.4f;
    /// <summary>How quickly speed falls off with size: speed = MaxSpeed / (1 + this * (sqrt(mass) - sqrt(MassMin))),
    /// clamped to [MinSpeed, MaxSpeed]. Higher = big cells slow down sooner.</summary>
    public float ScoreToSpeedMultiplier { get; set; } = 0.06f;

    // ---- Spikes (virus + saw) ----
    /// <summary>Mass per extra piece: a spike splits a cell into 2 + floor(mass / this) pieces...</summary>
    public float SpikySplitThreshold { get; set; } = 400f;
    /// <summary>...but never more than this many pieces in total.</summary>
    public int SpikySplitCount { get; set; } = 8;
    /// <summary>How many food pellets a spike hit throws out of the pieces it creates (paid for out of the cell's own mass).</summary>
    public int SpikyFoodCount { get; set; } = 8;
    /// <summary>Farthest a spike-thrown pellet can travel (each one flies 40-100% of this).</summary>
    public float SpikyFoodLaunchDistance { get; set; } = 40f;

    // ---- Map ----
    /// <summary>Size of the default ("Small") map in world units; the other MapSize choices are multiples of it.</summary>
    public float MapWidth { get; set; } = 1600f;
    public float MapHeight { get; set; } = 1600f;
    /// <summary>Hard cap on food items per room (each new client is sent all of them over UDP on join).</summary>
    public int MaxFoodCount { get; set; } = 20000;

    /// <summary>Repairs nonsense values (config typos) instead of letting them wedge the simulation.</summary>
    public void Normalize()
    {
        SplitForce = Math.Clamp(SplitForce, 0.1f, 10f);
        SplitDistance = Math.Max(0f, SplitDistance);
        SplitDistancePerScale = Math.Max(0f, SplitDistancePerScale);
        SplitSpeed = Math.Max(1f, SplitSpeed);
        MergeTimeMin = Math.Max(1f, MergeTimeMin);
        MergeTimeMax = Math.Max(MergeTimeMin, MergeTimeMax);
        MaxSpeed = Math.Max(0.5f, MaxSpeed);
        MinSpeed = Math.Clamp(MinSpeed, 0.1f, MaxSpeed);
        ScoreToSpeedMultiplier = Math.Max(0f, ScoreToSpeedMultiplier);
        SpikySplitThreshold = Math.Max(1f, SpikySplitThreshold);
        SpikySplitCount = Math.Clamp(SpikySplitCount, 2, Rules.MaxPiecesPerPlayer);
        SpikyFoodCount = Math.Clamp(SpikyFoodCount, 0, 64);
        SpikyFoodLaunchDistance = Math.Max(1f, SpikyFoodLaunchDistance);
        MapWidth = Math.Max(200f, MapWidth);
        MapHeight = Math.Max(200f, MapHeight);
        MaxFoodCount = Math.Max(50, MaxFoodCount);
    }
}
