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
}

public enum ServerMsg : byte
{
    Welcome = 1,
    Snapshot = 2,
    FoodFull = 3,
}

/// <summary>Tiny binary protocol - no NGO, no reflection, just BinaryReader/Writer over UDP payloads.</summary>
public static class Protocol
{
    public static byte[] EncodeWelcome(uint yourEntityId, int halfMapSize)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ServerMsg.Welcome);
        w.Write(yourEntityId);
        w.Write(halfMapSize);
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

        w.Write((ushort)(players.Count + bots.Count + viruses.Count + saws.Count));
        foreach (var p in players) WriteEntity(w, p.Id, p.Type, p.Position, p.Scale, p.Mass, p.Color, p.Name);
        foreach (var a in bots) WriteEntity(w, a.Id, a.Type, a.Position, a.Scale, a.Mass, a.Color, a.Name);
        foreach (var v in viruses) WriteEntity(w, v.Id, v.Type, v.Position, v.Scale, v.Mass, v.Color, v.Name);
        foreach (var s in saws) WriteEntity(w, s.Id, s.Type, s.Position, s.Scale, s.Mass, s.Color, s.Name);

        w.Write((ushort)changedFood.Count);
        foreach (var f in changedFood)
        {
            w.Write(f.Id);
            w.Write(f.Position.X);
            w.Write(f.Position.Y);
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

    public static bool TryDecodeClientMsg(byte[] data, out ClientMsg type, out JoinMsg join, out InputMsg input)
    {
        join = default;
        input = default;
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
