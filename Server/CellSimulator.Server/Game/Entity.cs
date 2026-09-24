using System.Numerics;

namespace CellSimulator.Server.Game;

public enum EntityType : byte
{
    Player = 0,
    Ai = 1,
    Virus = 2,
    Saw = 3,
    Powerup = 4,
}

public struct Rgba
{
    public byte R, G, B, A;

    /// <summary>A colour picked by a client is untrusted input: force it opaque and keep it away from
    /// the hazard colours (virus/saw green) so nobody can dress up as a spike, and off pure black/white
    /// so the blob and its outline stay visible on both the day and night backgrounds.</summary>
    public static Rgba Sanitize(Rgba c)
    {
        int r = Math.Clamp((int)c.R, 25, 235), g = Math.Clamp((int)c.G, 25, 235), b = Math.Clamp((int)c.B, 25, 235);
        bool spikeGreen = g > 150 && r < 110 && b < 130;
        if (spikeGreen) { r = 200; b = Math.Max(b, 150); }
        return new Rgba { R = (byte)r, G = (byte)g, B = (byte)b, A = 255 };
    }

    public static Rgba Random(Random rng)
    {
        return new Rgba
        {
            R = (byte)rng.Next(180, 256),
            G = (byte)rng.Next(180, 256),
            B = (byte)rng.Next(180, 256),
            A = 255,
        };
    }
}

public abstract class Entity
{
    public uint Id;
    public EntityType Type;
    public Vector2 Position;
    public float Mass = Rules.MassMin;
    public Rgba Color;
    public string Name = "";

    // The initial "just been split/popped" launch (GameWorld.StartLaunch/MoveEntity): a smoothstep
    // displacement of LaunchDistance along LaunchDir over LaunchDuration, ADDED to the entity's
    // normal steering - so a launched piece is still controllable and hands over to plain movement
    // without a speed jump. LaunchSource is the piece it was thrown from; the pair doesn't collide
    // while the launch runs (otherwise the source would be shoved off the spot it stayed on).
    public Vector2 LaunchDir;
    public float LaunchDistance;
    public float LaunchDuration;
    public float LaunchElapsed;
    public bool IsLaunching;
    public Entity? LaunchSource;

    // Per-entity spike-pop cooldown gate; only players/bots ever get hit, but lives on the base
    // type since the hazard pass iterates both uniformly.
    public DateTime LastSawHitUtc = DateTime.MinValue;

    // Timed power-up effects (see PowerupEntity). Players get them group-wide (GameWorld.GrantPowerup),
    // so clones inherit them; bots can collect them too.
    public DateTime SpeedBoostUntil = DateTime.MinValue;
    public DateTime ShieldUntil = DateTime.MinValue;
    public DateTime MagnetUntil = DateTime.MinValue;

    public bool HasSpeedBoost(DateTime now) => now < SpeedBoostUntil;
    public bool HasShield(DateTime now) => now < ShieldUntil;
    public bool HasMagnet(DateTime now) => now < MagnetUntil;

    /// <summary>Bit 1 = speed, 2 = shield, 4 = magnet: what's sent to clients so they can draw the glow.</summary>
    public byte EffectMask(DateTime now) =>
        (byte)((HasSpeedBoost(now) ? 1 : 0) | (HasShield(now) ? 2 : 0) | (HasMagnet(now) ? 4 : 0));

    public float Scale => Rules.CalculateScale(Mass);
}

public sealed class FoodItem
{
    public uint Id;
    public Vector2 Position;
    public Vector2 Velocity; // nonzero only for ejected mass, decays to zero then behaves like normal food

    // The direction this pellet was originally thrown in, if it was ejected - unlike Velocity,
    // this never decays, so GameWorld.ResolveSawFeeding still knows which way to launch a new saw
    // even after the pellet has slowed to a stop by the time it reaches one. Zero for ordinary
    // (non-ejected) food, which is what marks a pellet as "feed-eligible" for a saw.
    public Vector2 EjectDirection;

    // Seconds left before this pellet can be eaten. A freshly thrown pellet starts at the edge of
    // the piece that threw it (and of that piece's just-created siblings), so without a grace
    // period they'd swallow it again the very tick it appeared.
    public float EatImmunity;

    public const float Radius = 1.25f; // matches Food.SCALE / 2 in the original Unity project
}
