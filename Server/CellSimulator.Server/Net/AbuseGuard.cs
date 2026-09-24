using System.Collections.Concurrent;
using System.Net;

namespace CellSimulator.Server.Net;

/// <summary>
/// UDP has no handshake, so anyone can fire packets at the port. Three cheap limits keep one abusive
/// source from costing everyone else: a packet-rate token bucket per endpoint (a real client sends
/// ~50 inputs/s), a Join rate per source IP, and a cap on live sessions per source IP. The IP limits
/// are deliberately generous: mobile carriers and schools put many real players behind one address.
/// All thresholds come from configuration (Server:*) so they can be tightened without a rebuild.
/// </summary>
public sealed class AbuseGuard
{
    public int MaxPacketBytes { get; }
    public int PacketsPerSecond { get; }
    public int JoinsPerMinutePerIp { get; }
    public int MaxSessionsPerIp { get; }

    private sealed class Bucket
    {
        public double Tokens;
        public DateTime Last;
    }

    private readonly ConcurrentDictionary<IPEndPoint, Bucket> _packets = new();
    private readonly ConcurrentDictionary<IPAddress, Queue<DateTime>> _joins = new();

    public AbuseGuard(int maxPacketBytes = 512, int packetsPerSecond = 200, int joinsPerMinutePerIp = 20, int maxSessionsPerIp = 30)
    {
        MaxPacketBytes = maxPacketBytes;
        PacketsPerSecond = packetsPerSecond;
        JoinsPerMinutePerIp = joinsPerMinutePerIp;
        MaxSessionsPerIp = maxSessionsPerIp;
    }

    /// <summary>False for oversized datagrams and for endpoints exceeding their packet budget
    /// (a burst of up to one second's worth is allowed).</summary>
    public bool AllowPacket(IPEndPoint from, int length, DateTime now)
    {
        if (length > MaxPacketBytes) return false;

        var bucket = _packets.GetOrAdd(from, _ => new Bucket { Tokens = PacketsPerSecond, Last = now });
        lock (bucket)
        {
            bucket.Tokens = Math.Min(PacketsPerSecond, bucket.Tokens + (now - bucket.Last).TotalSeconds * PacketsPerSecond);
            bucket.Last = now;
            if (bucket.Tokens < 1) return false;
            bucket.Tokens -= 1;
            return true;
        }
    }

    /// <summary>Whether a NEW session may be created for this source address, given how many it already has.</summary>
    public bool AllowJoin(IPAddress ip, int currentSessionsFromIp, DateTime now)
    {
        if (currentSessionsFromIp >= MaxSessionsPerIp) return false;

        var recent = _joins.GetOrAdd(ip, _ => new Queue<DateTime>());
        lock (recent)
        {
            while (recent.Count > 0 && now - recent.Peek() > TimeSpan.FromMinutes(1)) recent.Dequeue();
            if (recent.Count >= JoinsPerMinutePerIp) return false;
            recent.Enqueue(now);
            return true;
        }
    }

    /// <summary>Drops bookkeeping for sources that went quiet, so the tables can't grow forever.</summary>
    public void Prune(DateTime now)
    {
        foreach (var (endPoint, bucket) in _packets)
        {
            lock (bucket)
            {
                if (now - bucket.Last > TimeSpan.FromMinutes(2)) _packets.TryRemove(endPoint, out _);
            }
        }

        foreach (var (ip, recent) in _joins)
        {
            lock (recent)
            {
                if (recent.Count == 0 || now - recent.Peek() > TimeSpan.FromMinutes(1)) _joins.TryRemove(ip, out _);
            }
        }
    }
}
