using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Decodes the server's snapshot packets (Server/CellSimulator.Server/Net/Protocol.cs). Deliberately
// plain C# with no UnityEngine dependency: the very same file is compiled into the server's test
// project (see CellSimulator.Server.Tests.csproj), so every change to the wire format is checked
// against the real client decoder instead of a copy of it.
//
// Two formats are understood, because a new client can meet an old server:
//   ServerMsg.Snapshot (2)   - the original full-precision format.
//   ServerMsg.SnapshotV2 (6) - compact: positions quantised relative to a per-packet origin, and
//                              each entity's name/colour sent only occasionally (this decoder
//                              remembers them in between).
public struct DecodedEntity
{
    public uint Id;
    public byte Type;       // 0 player, 1 bot, 2 virus, 3 saw, 4 power-up
    public float X, Y;
    public float Scale;
    public float Mass;
    public byte R, G, B, A;
    public string Name;
    public bool HasInfo;    // false only in the rare case a compact snapshot mentions an entity whose name/colour was never received
    public byte Effects;    // bit 1 speed, 2 shield, 4 magnet
}

public struct DecodedFood
{
    public uint Id;
    public float X, Y;
}

public sealed class DecodedSnapshot
{
    public uint Tick;
    public List<KeyValuePair<string, float>> Leaderboard;   // null when this packet didn't carry one (compact format sends it at ~2 Hz)
    public List<DecodedEntity> Entities = new List<DecodedEntity>();
    public List<DecodedFood> Food = new List<DecodedFood>();
    public Dictionary<uint, uint> GroupOf = new Dictionary<uint, uint>();   // player entity id -> owning player's session id (only for split players)
    public bool IsCompact;
}

public sealed class SnapshotDecoder
{
    public const byte SnapshotType = 2;
    public const byte SnapshotV2Type = 6;

    private const float PositionScale = 20f;   // compact format: 1 unit = 20 steps of a 16-bit offset
    private const float ScaleScale = 100f;
    private const int MaxInfoCache = 4096;

    private struct Info
    {
        public byte R, G, B, A;
        public string Name;
    }

    private readonly Dictionary<uint, Info> infoCache = new Dictionary<uint, Info>();

    /// <summary>Half extents of the map (from Welcome); compact-format food positions are fractions of these.</summary>
    public float HalfWidth = 100f;
    public float HalfHeight = 100f;

    public static bool IsSnapshot(byte firstByte)
    {
        return firstByte == SnapshotType || firstByte == SnapshotV2Type;
    }

    /// <summary>Throws EndOfStreamException on a truncated/malformed packet (the caller drops it).</summary>
    public DecodedSnapshot Decode(byte[] data)
    {
        using (var ms = new MemoryStream(data))
        using (var r = new BinaryReader(ms))
        {
            byte type = r.ReadByte();
            return type == SnapshotV2Type ? DecodeCompact(r, ms) : DecodeFull(r, ms);
        }
    }

    // ---- original format -------------------------------------------------------------------

    private DecodedSnapshot DecodeFull(BinaryReader r, MemoryStream ms)
    {
        var snap = new DecodedSnapshot();
        snap.Tick = r.ReadUInt32();

        byte leaderboardCount = r.ReadByte();
        snap.Leaderboard = new List<KeyValuePair<string, float>>(leaderboardCount);
        for (int i = 0; i < leaderboardCount; i++)
        {
            string name = ReadString(r);
            snap.Leaderboard.Add(new KeyValuePair<string, float>(name, r.ReadSingle()));
        }

        ushort entityCount = r.ReadUInt16();
        for (int i = 0; i < entityCount; i++)
        {
            var e = new DecodedEntity();
            e.Id = r.ReadUInt32();
            e.Type = r.ReadByte();
            e.X = r.ReadSingle();
            e.Y = r.ReadSingle();
            e.Scale = r.ReadSingle();
            e.Mass = r.ReadSingle();
            e.R = r.ReadByte(); e.G = r.ReadByte(); e.B = r.ReadByte(); e.A = r.ReadByte();
            e.Name = ReadString(r);
            e.HasInfo = true;
            snap.Entities.Add(e);
        }

        ushort foodCount = r.ReadUInt16();
        for (int i = 0; i < foodCount; i++)
        {
            var f = new DecodedFood();
            f.Id = r.ReadUInt32();
            f.X = r.ReadSingle();
            f.Y = r.ReadSingle();
            snap.Food.Add(f);
        }

        // Trailing sections (absent from older servers).
        if (ms.Position + 2 <= ms.Length)
        {
            ushort owners = r.ReadUInt16();
            for (int i = 0; i < owners; i++)
            {
                uint entity = r.ReadUInt32();
                snap.GroupOf[entity] = r.ReadUInt32();
            }
        }

        return snap;
    }

    // ---- compact format -------------------------------------------------------------------------

    private DecodedSnapshot DecodeCompact(BinaryReader r, MemoryStream ms)
    {
        var snap = new DecodedSnapshot();
        snap.IsCompact = true;
        snap.Tick = r.ReadUInt32();
        float originX = r.ReadSingle();
        float originY = r.ReadSingle();
        byte flags = r.ReadByte();

        if ((flags & 1) != 0)
        {
            byte leaderboardCount = r.ReadByte();
            snap.Leaderboard = new List<KeyValuePair<string, float>>(leaderboardCount);
            for (int i = 0; i < leaderboardCount; i++)
            {
                string name = ReadString(r);
                snap.Leaderboard.Add(new KeyValuePair<string, float>(name, r.ReadSingle()));
            }
        }

        if (infoCache.Count > MaxInfoCache)
        {
            infoCache.Clear(); // everything is re-announced within a few seconds anyway
        }

        ushort entityCount = r.ReadUInt16();
        for (int i = 0; i < entityCount; i++)
        {
            byte typeAndFlags = r.ReadByte();
            var e = new DecodedEntity();
            e.Type = (byte)(typeAndFlags & 0x07);
            bool hasFullInfo = (typeAndFlags & 0x08) != 0;
            e.Id = r.ReadUInt32();
            e.X = originX + r.ReadInt16() / PositionScale;
            e.Y = originY + r.ReadInt16() / PositionScale;
            e.Scale = r.ReadUInt16() / ScaleScale;
            e.Mass = r.ReadSingle();

            if (hasFullInfo)
            {
                var info = new Info();
                info.R = r.ReadByte(); info.G = r.ReadByte(); info.B = r.ReadByte(); info.A = r.ReadByte();
                info.Name = ReadString(r);
                infoCache[e.Id] = info;
            }

            Info known;
            if (infoCache.TryGetValue(e.Id, out known))
            {
                e.R = known.R; e.G = known.G; e.B = known.B; e.A = known.A;
                e.Name = known.Name;
                e.HasInfo = true;
            }
            else
            {
                e.R = e.G = e.B = 200; e.A = 255;
                e.Name = "";
                e.HasInfo = false;
            }

            snap.Entities.Add(e);
        }

        ushort foodCount = r.ReadUInt16();
        for (int i = 0; i < foodCount; i++)
        {
            var f = new DecodedFood();
            f.Id = r.ReadUInt16();
            f.X = r.ReadInt16() / 32767f * HalfWidth;
            f.Y = r.ReadInt16() / 32767f * HalfHeight;
            snap.Food.Add(f);
        }

        ushort owners = r.ReadUInt16();
        for (int i = 0; i < owners; i++)
        {
            uint entity = r.ReadUInt32();
            snap.GroupOf[entity] = r.ReadUInt32();
        }

        ushort effects = r.ReadUInt16();
        var effectOf = new Dictionary<uint, byte>();
        for (int i = 0; i < effects; i++)
        {
            uint entity = r.ReadUInt32();
            effectOf[entity] = r.ReadByte();
        }
        if (effectOf.Count > 0)
        {
            for (int i = 0; i < snap.Entities.Count; i++)
            {
                byte mask;
                if (effectOf.TryGetValue(snap.Entities[i].Id, out mask))
                {
                    var e = snap.Entities[i];
                    e.Effects = mask;
                    snap.Entities[i] = e;
                }
            }
        }

        return snap;
    }

    private static string ReadString(BinaryReader r)
    {
        byte len = r.ReadByte();
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }
}

/// <summary>The one-off "you died" message (ServerMsg.Died).</summary>
public struct DeathReport
{
    public string KillerName;   // empty when the cause wasn't another cell
    public float PeakMass;
    public float SurvivedSeconds;
    public int FoodEaten;
    public int BlobsEaten;
    public int SpikesHit;

    public static DeathReport Decode(BinaryReader r)
    {
        var d = new DeathReport();
        byte len = r.ReadByte();
        d.KillerName = Encoding.UTF8.GetString(r.ReadBytes(len));
        d.PeakMass = r.ReadSingle();
        d.SurvivedSeconds = r.ReadSingle();
        d.FoodEaten = r.ReadUInt16();
        d.BlobsEaten = r.ReadUInt16();
        d.SpikesHit = r.ReadUInt16();
        return d;
    }
}
