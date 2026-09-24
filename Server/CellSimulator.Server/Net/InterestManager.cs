using System.Numerics;
using CellSimulator.Server.Game;

namespace CellSimulator.Server.Net;

/// <summary>
/// Decides what each player's snapshot contains. Sending the whole room to everyone stopped
/// working once the map grew: 26 bots + 18 spikes + players push a snapshot past the ~1470-byte
/// UDP payload that fits one network packet, and fragmented UDP is routinely dropped (mobile
/// carriers especially) - the client would just stop receiving updates. So each viewer only gets
/// what its camera can actually see (plus a margin), nearest first, hard-capped at
/// <see cref="MaxPacketBytes"/>. The client already deletes anything absent from a snapshot, so
/// entities leaving view simply vanish off-screen and reappear when they come back.
/// Food changes are NOT culled: the client only ever hears about a pellet's position when it
/// changes, so skipping an update would strand a ghost pellet at its old spot.
/// </summary>
public static class InterestManager
{
    /// <summary>Stays under a 1500-byte MTU with IP/UDP headers and a little tunnel overhead to spare.</summary>
    public const int MaxPacketBytes = 1400;

    // Widest realistic phone screen: half-diagonal is at most ~2.6x the camera's orthographic size.
    private const float ScreenReachPerOrtho = 2.8f;
    private const float ViewMargin = 30f;

    // The client's camera zooms out instantly-ish but back in slowly (PlayerBlob lerps at 3/s),
    // so after a split (smaller primary piece) it still shows a bigger area for a moment. Track the
    // zoom with a slow decay so the culling radius never shrinks faster than the camera does.
    private const float OrthoDecayPerSecond = 0.5f;

    /// <summary>Mirrors PlayerBlob.UpdateOrthographicSize plus its group-framing rule: what
    /// orthographic size the viewer's camera wants for these pieces.</summary>
    public static float WantedOrthoSize(IReadOnlyList<PlayerEntity> own, Vector2 centre, float extent)
    {
        float biggest = own.Max(p => p.Scale);
        float forPrimary = MathF.Max((biggest + 26f) / 1.4f, 20f);
        return MathF.Max(forPrimary, extent * 1.4f);
    }

    public static byte[] Encode(
        uint tick,
        uint sessionId,
        IReadOnlyList<Entity> everything,
        IReadOnlyCollection<FoodItem> changedFood,
        List<(string Name, float Mass)> leaderboard,
        ref float orthoState,
        float dt)
    {
        var own = new List<PlayerEntity>();
        foreach (var e in everything)
        {
            if (e is PlayerEntity p && p.GroupId == sessionId) own.Add(p);
        }

        // Unknown viewer (shouldn't happen for a live session): fall back to the size-capped full view.
        List<(Entity Entity, float Distance)> candidates;
        if (own.Count == 0)
        {
            candidates = everything.Select(e => (e, 0f)).ToList();
        }
        else
        {
            float totalMass = own.Sum(p => p.Mass);
            var centre = Vector2.Zero;
            foreach (var p in own) centre += p.Position * (p.Mass / totalMass);
            float extent = own.Max(p => Vector2.Distance(centre, p.Position) + p.Scale / 2f);

            float wanted = WantedOrthoSize(own, centre, extent);
            orthoState = orthoState <= 0f ? wanted : MathF.Max(wanted, orthoState - orthoState * OrthoDecayPerSecond * dt);
            float radius = orthoState * ScreenReachPerOrtho + ViewMargin;

            candidates = new List<(Entity, float)>(everything.Count);
            foreach (var e in everything)
            {
                bool isOwn = e is PlayerEntity p && p.GroupId == sessionId;
                float distance = isOwn ? float.MinValue : Vector2.Distance(centre, e.Position) - e.Scale / 2f;
                if (isOwn || distance <= radius) candidates.Add((e, distance));
            }
            candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance)); // own pieces first, then nearest first
        }

        // Fit the MTU budget: shed the farthest entities (never the viewer's own) until it does.
        int keep = candidates.Count;
        while (true)
        {
            var packet = Protocol.EncodeSnapshot(tick, candidates.Take(keep).Select(c => c.Entity).ToList(), changedFood, leaderboard);
            if (packet.Length <= MaxPacketBytes || keep <= own.Count) return packet;
            keep = Math.Max(own.Count, keep * 3 / 4);
        }
    }
}
