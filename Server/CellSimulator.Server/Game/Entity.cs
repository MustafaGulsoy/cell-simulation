using System.Numerics;

namespace CellSimulator.Server.Game;

public enum EntityType : byte
{
    Player = 0,
    Ai = 1,
    Virus = 2,
    Saw = 3,
}

public struct Rgba
{
    public byte R, G, B, A;

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

    // Decaying outward impulse used by GameWorld.ResolveSplitSeparation's ongoing "keep siblings
    // apart" spring (NOT the initial split launch - that's the Launch* fields below). Only
    // PlayerEntity ever sets this, but living on the base type keeps MoveEntity<T> generic.
    public Vector2 SplitVelocity;

    // The initial "just been split/popped" launch: a size-proportional lerp from LaunchStart to
    // LaunchTarget eased fast-then-slow (GameWorld.StartLaunch/MoveEntity), not a physics
    // simulation - while IsLaunching is true this fully overrides normal movement for the entity.
    public Vector2 LaunchStart;
    public Vector2 LaunchTarget;
    public float LaunchElapsed;
    public bool IsLaunching;

    // Per-entity saw-pop cooldown gate; only players/bots ever get hit, but lives on the base
    // type since ResolveSawCollisions iterates AllBlobs() (both) uniformly.
    public DateTime LastSawHitUtc = DateTime.MinValue;

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

    public const float Radius = 1.25f; // matches Food.SCALE / 2 in the original Unity project
}
