using System.Numerics;

namespace CellSimulator.Server.Game;

public enum EntityType : byte
{
    Player = 0,
    Ai = 1,
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

    public float Scale => Rules.CalculateScale(Mass);
}

public sealed class FoodItem
{
    public uint Id;
    public Vector2 Position;
    public const float Radius = 1.25f; // matches Food.SCALE / 2 in the original Unity project
}
