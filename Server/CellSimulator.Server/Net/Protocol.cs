using System.Numerics;
using System.Text;
using CellSimulator.Server.Game;

namespace CellSimulator.Server.Net;

public enum ClientMsg : byte
{
    Join = 1,
    Input = 2,
    Split = 3,
    Eject = 4,
    Emoji = 5,
}

public enum ServerMsg : byte
{
    Welcome = 1,
    Snapshot = 2,
    FoodFull = 3,
    EmojiEvent = 4,
}

/// <summary>Tiny binary protocol - no NGO, no reflection, just BinaryReader/Writer over UDP payloads.</summary>
public static class Protocol
{
    /// <summary>[type][entityId][halfWidth][halfHeight]. Older clients only read the first three
    /// fields (they treat the map as a square of halfWidth) and ignore the trailing halfHeight, so
    /// appending it is backwards compatible.</summary>
    public static byte[] EncodeWelcome(uint yourEntityId, int halfWidth, int halfHeight)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ServerMsg.Welcome);
        w.Write(yourEntityId);
        w.Write(halfWidth);
        w.Write(halfHeight);
        return ms.ToArray();
    }

    public static byte[] EncodeSnapshot(
        uint tick,
        IReadOnlyCollection<PlayerEntity> players,
        IReadOnlyCollection<AiEntity> bots,
        IReadOnlyCollection<VirusEntity> viruses,
        IReadOnlyCollection<SawEntity> saws,
        IReadOnlyCollection<FoodItem> changedFood,
        List<(string Name, float Mass)> leaderboard)
    {
        var everything = new List<Entity>(players.Count + bots.Count + viruses.Count + saws.Count);
        everything.AddRange(players);
        everything.AddRange(bots);
        everything.AddRange(viruses);
        everything.AddRange(saws);
        return EncodeSnapshot(tick, everything, changedFood, leaderboard);
    }

    /// <summary>The snapshot for one viewer: whatever subset of the world (see InterestManager) is
    /// passed in as <paramref name="entities"/>, plus every food change.</summary>
    public static byte[] EncodeSnapshot(
        uint tick,
        IReadOnlyCollection<Entity> entities,
        IReadOnlyCollection<FoodItem> changedFood,
        List<(string Name, float Mass)> leaderboard)
    {
        var players = entities.OfType<PlayerEntity>().ToList();

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ServerMsg.Snapshot);
        w.Write(tick);

        w.Write((byte)leaderboard.Count);
        foreach (var (name, mass) in leaderboard)
        {
            WriteString(w, name);
            w.Write(mass);
        }

        w.Write((ushort)entities.Count);
        foreach (var e in entities) WriteEntity(w, e.Id, e.Type, e.Position, e.Scale, e.Mass, e.Color, e.Name);

        w.Write((ushort)changedFood.Count);
        foreach (var f in changedFood)
        {
            w.Write(f.Id);
            w.Write(f.Position.X);
            w.Write(f.Position.Y);
        }

        // Trailing section (old clients stop reading before it): which player entities belong to
        // which player, for every player that's currently split. Lets a client find ALL of its own
        // pieces (GroupId == its session id) to frame the camera on the whole group.
        var splitOwners = players.GroupBy(p => p.GroupId).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        w.Write((ushort)splitOwners.Count);
        foreach (var p in splitOwners)
        {
            w.Write(p.Id);
            w.Write(p.GroupId);
        }

        return ms.ToArray();
    }

    /// <summary>Sent once (in chunks) right after Welcome so a newly-joined client has every food position;
    /// Snapshot only ever carries the food that changed that tick.</summary>
    public static byte[] EncodeFoodChunk(IEnumerable<FoodItem> chunk)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ServerMsg.FoodFull);
        var list = chunk as ICollection<FoodItem> ?? chunk.ToList();
        w.Write((ushort)list.Count);
        foreach (var f in list)
        {
            w.Write(f.Id);
            w.Write(f.Position.X);
            w.Write(f.Position.Y);
        }
        return ms.ToArray();
    }

    /// <summary>Relayed immediately by UdpServerService when it gets a ClientMsg.Emoji, outside the
    /// tick loop entirely - never batched into Snapshot, so it costs nothing on the hot path.</summary>
    public static byte[] EncodeEmojiEvent(uint entityId, byte emojiId)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ServerMsg.EmojiEvent);
        w.Write(entityId);
        w.Write(emojiId);
        return ms.ToArray();
    }

    private static void WriteEntity(BinaryWriter w, uint id, EntityType type, Vector2 pos, float scale, float mass, Rgba color, string name)
    {
        w.Write(id);
        w.Write((byte)type);
        w.Write(pos.X);
        w.Write(pos.Y);
        w.Write(scale);
        w.Write(mass);
        w.Write(color.R); w.Write(color.G); w.Write(color.B); w.Write(color.A);
        WriteString(w, name);
    }

    private static void WriteString(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length > 255) Array.Resize(ref bytes, 255);
        w.Write((byte)bytes.Length);
        w.Write(bytes);
    }

    private static string ReadString(BinaryReader r)
    {
        byte len = r.ReadByte();
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    public readonly record struct JoinMsg(string Username, MapSize MapSize);
    public readonly record struct InputMsg(Vector2 Direction);
    public readonly record struct EmojiMsg(byte EmojiId);

    public static bool TryDecodeClientMsg(byte[] data, out ClientMsg type, out JoinMsg join, out InputMsg input, out EmojiMsg emoji)
    {
        join = default;
        input = default;
        emoji = default;
        type = default;
        if (data.Length < 1) return false;

        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        type = (ClientMsg)r.ReadByte();

        try
        {
            switch (type)
            {
                case ClientMsg.Join:
                    string username = ReadString(r);
                    var mapSize = (MapSize)r.ReadByte();
                    join = new JoinMsg(username, mapSize);
                    return true;
                case ClientMsg.Input:
                    input = new InputMsg(new Vector2(r.ReadSingle(), r.ReadSingle()));
                    return true;
                case ClientMsg.Split:
                case ClientMsg.Eject:
                    return true;
                case ClientMsg.Emoji:
                    emoji = new EmojiMsg(r.ReadByte());
                    return true;
                default:
                    return false;
            }
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }
}
