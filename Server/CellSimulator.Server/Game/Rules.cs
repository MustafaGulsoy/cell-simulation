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

    public static float CalculateScale(float mass)
    {
        float c = MathF.Sqrt(mass * ScaleMultiplier);
        return Math.Clamp(c, BlobScaleMin, BlobScaleMax);
    }

    public static float ClampMass(float mass) => Math.Clamp(mass, MassMin, MassMax);

    /// <summary>True if an entity with eaterScale can eat one with preyScale (must be at least ~15% bigger).</summary>
    public static bool CanEat(float eaterScale, float preyScale) => eaterScale > preyScale * ScaleMultiplier;

    public static float MovementSpeedForScale(float scale)
    {
        float speed = MathF.Sqrt(5000f / ((scale + 6f) * 0.01f));
        return Math.Clamp(speed, MovementSpeedMin, MovementSpeedMax);
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
    public static int SideLength(MapSize size) => size switch
    {
        MapSize.Huge => 1000,
        MapSize.Large => 600,
        MapSize.Medium => 400,
        MapSize.Small => 200,
        _ => 200,
    };
}
