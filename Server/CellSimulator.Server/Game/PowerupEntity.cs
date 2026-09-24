namespace CellSimulator.Server.Game;

public enum PowerupKind : byte
{
    Speed = 1,
    Shield = 2,
    Magnet = 3,
}

/// <summary>A pickup lying on the map. Touching it (player piece or bot) grants the timed effect to
/// the whole player group; it then goes dormant (not sent, not collectable) until RespawnAtUtc and
/// reappears somewhere else. The kind travels as the entity's Name so the snapshot format needs no
/// new field; only clients announcing protocol version >= 2 are sent power-ups at all.</summary>
public sealed class PowerupEntity : Entity
{
    public PowerupKind Kind;
    public DateTime RespawnAtUtc = DateTime.MinValue;

    public PowerupEntity()
    {
        Type = EntityType.Powerup;
    }

    public bool IsActive(DateTime now) => now >= RespawnAtUtc;

    public static string NameOf(PowerupKind kind) => kind switch
    {
        PowerupKind.Speed => "speed",
        PowerupKind.Shield => "shield",
        _ => "magnet",
    };

    public static Rgba ColorOf(PowerupKind kind) => kind switch
    {
        PowerupKind.Speed => new Rgba { R = 255, G = 210, B = 60, A = 255 },
        PowerupKind.Shield => new Rgba { R = 90, G = 170, B = 255, A = 255 },
        _ => new Rgba { R = 230, G = 90, B = 200, A = 255 },
    };
}
