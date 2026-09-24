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
    /// <summary>[6][u32 client timestamp] - echoed back as Pong so the client can show its ping.</summary>
    Ping = 6,
}

public enum ServerMsg : byte
{
    Welcome = 1,
    Snapshot = 2,
    FoodFull = 3,
    EmojiEvent = 4,
    /// <summary>Sent once to the player whose last cell was just eaten (see EncodeDied).</summary>
    Died = 5,
    /// <summary>Compact snapshot for clients that announced version >= 2 (see SnapshotV2).</summary>
    SnapshotV2 = 6,
    Pong = 7,
}

/// <summary>Tiny binary protocol - no NGO, no reflection, just BinaryReader/Writer over UDP payloads.</summary>
public static class Protocol
{
    /// <summary>Highest protocol version this server speaks; a client's Join announces what it understands.</summary>
    public const byte CurrentVersion = 2;

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

    /// <summary>[type=5][killer name][peak mass f32][seconds survived f32][food u16][cells eaten u16][spikes hit u16]:
    /// the end-of-life summary sent once to the player whose last cell was just eaten.</summary>
    public static byte[] EncodeDied(DeathInfo death)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ServerMsg.Died);
        WriteString(w, death.KillerName ?? "");
        w.Write(death.PeakMass);
        w.Write(death.SurvivedSeconds);
        w.Write((ushort)Math.Min(death.FoodEaten, ushort.MaxValue));
        w.Write((ushort)Math.Min(death.BlobsEaten, ushort.MaxValue));
        w.Write((ushort)Math.Min(death.SpikesHit, ushort.MaxValue));
        return ms.ToArray();
    }

    // ---- compact snapshot (protocol version 2) -------------------------------------------------------

    /// <summary>How often (in ticks) an entity's name/colour is re-sent to a client that already has it,
    /// so a client that missed the first packet (UDP) still learns it within ~3 s at 30 Hz.</summary>
    public const uint InfoResendTicks = 90;

    /// <summary>How often (in ticks) the leaderboard rides along (2 Hz is plenty for a scoreboard).</summary>
    public const uint LeaderboardTicks = 15;

    private const float PositionScale = 20f;
    private const float ScaleScale = 100f;

    /// <summary>
    /// Compact snapshot: [6][tick][originX][originY][flags][leaderboard?][entities][food][owners][effects].
    /// Entity positions are 16-bit offsets (1/20 unit) from the per-packet origin (the viewer's centre -
    /// entities are culled to a radius around it, so they always fit); scale is a u16 (1/100);
    /// name+colour are only included when the client hasn't been told them recently
    /// (SessionState.LastFullInfoTick). Food is 6 bytes instead of 12: a u16 id (food ids are the first
    /// ones ever allocated) and positions as fractions of the map's half extents.
    /// </summary>
    public static byte[] EncodeSnapshotV2(
        uint tick,
        IReadOnlyList<Entity> entities,
        IReadOnlyCollection<FoodItem> changedFood,
        List<(string Name, float Mass)> leaderboard,
        Vector2 origin,
        SessionState session,
        float halfWidth,
        float halfHeight)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ServerMsg.SnapshotV2);
        w.Write(tick);
        w.Write(origin.X);
        w.Write(origin.Y);

        bool sendLeaderboard = tick - session.LastLeaderboardTick >= LeaderboardTicks || session.LastLeaderboardTick == 0;
        w.Write((byte)(sendLeaderboard ? 1 : 0));
        if (sendLeaderboard)
        {
            w.Write((byte)leaderboard.Count);
            foreach (var (name, mass) in leaderboard)
            {
                WriteString(w, name);
                w.Write(mass);
            }
        }

        var now = DateTime.UtcNow;
        var effects = new List<(uint Id, byte Mask)>();

        w.Write((ushort)entities.Count);
        foreach (var e in entities)
        {
            bool full = !session.LastFullInfoTick.TryGetValue(e.Id, out var last) || tick - last >= InfoResendTicks;
            w.Write((byte)((byte)e.Type | (full ? 0x08 : 0)));
            w.Write(e.Id);
            w.Write(QuantiseOffset(e.Position.X - origin.X));
            w.Write(QuantiseOffset(e.Position.Y - origin.Y));
            w.Write((ushort)Math.Clamp((int)MathF.Round(e.Scale * ScaleScale), 0, ushort.MaxValue));
            w.Write(e.Mass);
            if (full)
            {
                w.Write(e.Color.R); w.Write(e.Color.G); w.Write(e.Color.B); w.Write(e.Color.A);
                WriteString(w, e.Name);
            }

            byte mask = e.EffectMask(now);
            if (mask != 0) effects.Add((e.Id, mask));
        }

        var food = changedFood.Where(f => f.Id <= ushort.MaxValue).ToList();
        w.Write((ushort)food.Count);
        foreach (var f in food)
        {
            w.Write((ushort)f.Id);
            w.Write(QuantiseFraction(f.Position.X, halfWidth));
            w.Write(QuantiseFraction(f.Position.Y, halfHeight));
        }

        var owners = entities.OfType<PlayerEntity>().GroupBy(p => p.GroupId).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        w.Write((ushort)owners.Count);
        foreach (var p in owners)
        {
            w.Write(p.Id);
            w.Write(p.GroupId);
        }

        w.Write((ushort)effects.Count);
        foreach (var (id, mask) in effects)
        {
            w.Write(id);
            w.Write(mask);
        }

        return ms.ToArray();
    }

    /// <summary>Records that this packet (about to be sent) carried name/colour for the flagged entities,
    /// and that it carried the leaderboard - call only for a packet that will actually be sent.</summary>
    public static void MarkSent(uint tick, IReadOnlyList<Entity> entities, SessionState session)
    {
        bool sentLeaderboard = tick - session.LastLeaderboardTick >= LeaderboardTicks || session.LastLeaderboardTick == 0;
        if (sentLeaderboard) session.LastLeaderboardTick = tick;

        foreach (var e in entities)
        {
            if (!session.LastFullInfoTick.TryGetValue(e.Id, out var last) || tick - last >= InfoResendTicks)
            {
                session.LastFullInfoTick[e.Id] = tick;
            }
        }

        // Forget entities that have been gone a while (their ids are never reused), so this can't grow forever.
        if (session.LastFullInfoTick.Count > 2048)
        {
            foreach (var stale in session.LastFullInfoTick.Where(kv => tick - kv.Value > InfoResendTicks * 4).Select(kv => kv.Key).ToList())
            {
                session.LastFullInfoTick.Remove(stale);
            }
        }
    }

    private static short QuantiseOffset(float units) =>
        (short)Math.Clamp((int)MathF.Round(units * PositionScale), short.MinValue, short.MaxValue);

    private static short QuantiseFraction(float position, float half) =>
        (short)Math.Clamp((int)MathF.Round(position / MathF.Max(half, 1f) * 32767f), -32767, 32767);

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

    /// <summary>ClientVersion 0 = a client that predates versioning (sends nothing after the map
    /// size). New features that older clients can't render (power-ups, compact snapshots) are only
    /// sent to clients that announce a version that understands them. PreferredColor is optional.</summary>
    public readonly record struct JoinMsg(string Username, MapSize MapSize, byte ClientVersion = 0, Rgba? PreferredColor = null);
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

                    // Optional trailing fields (absent from clients that predate them):
                    // [version][r][g][b] - the player's chosen colour, sent by version >= 2.
                    byte version = 0;
                    Rgba? color = null;
                    if (ms.Position < ms.Length) version = r.ReadByte();
                    if (version >= 2 && ms.Length - ms.Position >= 3)
                    {
                        color = new Rgba { R = r.ReadByte(), G = r.ReadByte(), B = r.ReadByte(), A = 255 };
                    }
                    join = new JoinMsg(username, mapSize, version, color);
                    return true;
                case ClientMsg.Input:
                    input = new InputMsg(new Vector2(r.ReadSingle(), r.ReadSingle()));
                    return true;
                case ClientMsg.Split:
                case ClientMsg.Eject:
                    return true;
                case ClientMsg.Ping:
                    return data.Length >= 5; // the 4-byte timestamp is echoed straight from the raw packet
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
