using System.Globalization;
using System.Text;

namespace CellSimulator.Server;

/// <summary>
/// Process-wide health numbers for /stats and /metrics (Prometheus text format): how long game
/// ticks take (the 33 ms budget at 30 Hz is the number that matters), how big the snapshots we send
/// are (they must stay under one network packet), and running counters. Cheap enough to update on
/// the hot path: counters are Interlocked, timings go into small ring buffers under a short lock.
/// </summary>
public static class ServerMetrics
{
    private const int Window = 300; // ~10 s of ticks

    private static long _packetsIn, _packetsRejected, _packetsOut, _bytesOut, _joins, _joinsRejected, _deaths;
    private static readonly object Gate = new();
    private static readonly double[] TickMs = new double[Window];
    private static readonly int[] SnapshotBytes = new int[Window];
    private static int _tickCount, _snapshotCount;
    private static long _slowTicks;

    public static void PacketIn() => Interlocked.Increment(ref _packetsIn);
    public static void PacketRejected() => Interlocked.Increment(ref _packetsRejected);
    public static void PacketOut(int bytes) { Interlocked.Increment(ref _packetsOut); Interlocked.Add(ref _bytesOut, bytes); }
    public static void Join() => Interlocked.Increment(ref _joins);
    public static void JoinRejected() => Interlocked.Increment(ref _joinsRejected);
    public static void Death() => Interlocked.Increment(ref _deaths);

    /// <summary>Records how long one full loop iteration (simulate + encode + send) took.</summary>
    public static void Tick(double milliseconds, double budgetMs = 1000.0 / 30.0)
    {
        lock (Gate)
        {
            TickMs[_tickCount++ % Window] = milliseconds;
            if (milliseconds > budgetMs) _slowTicks++;
        }
    }

    public static void Snapshot(int bytes)
    {
        lock (Gate) SnapshotBytes[_snapshotCount++ % Window] = bytes;
    }

    public sealed record Report(
        double TickAvgMs, double TickMaxMs, double TickP99Ms, long SlowTicks,
        double SnapshotAvgBytes, int SnapshotMaxBytes,
        long PacketsIn, long PacketsRejected, long PacketsOut, long BytesOut,
        long Joins, long JoinsRejected, long Deaths);

    public static Report Read()
    {
        lock (Gate)
        {
            var ticks = TickMs.Take(Math.Min(_tickCount, Window)).OrderBy(x => x).ToArray();
            var snaps = SnapshotBytes.Take(Math.Min(_snapshotCount, Window)).ToArray();
            return new Report(
                ticks.Length == 0 ? 0 : ticks.Average(),
                ticks.Length == 0 ? 0 : ticks[^1],
                ticks.Length == 0 ? 0 : ticks[(int)(ticks.Length * 0.99)],
                _slowTicks,
                snaps.Length == 0 ? 0 : snaps.Average(),
                snaps.Length == 0 ? 0 : snaps.Max(),
                Interlocked.Read(ref _packetsIn), Interlocked.Read(ref _packetsRejected),
                Interlocked.Read(ref _packetsOut), Interlocked.Read(ref _bytesOut),
                Interlocked.Read(ref _joins), Interlocked.Read(ref _joinsRejected), Interlocked.Read(ref _deaths));
        }
    }

    /// <summary>Prometheus exposition format: scrape it, or just curl it.</summary>
    public static string ToPrometheus(int rooms, int players)
    {
        var r = Read();
        var sb = new StringBuilder();
        void Metric(string name, string type, string help, double value)
        {
            sb.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
            sb.Append("# TYPE ").Append(name).Append(' ').AppendLine(type);
            sb.Append(name).Append(' ').AppendLine(value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        Metric("blob_rooms", "gauge", "Active rooms", rooms);
        Metric("blob_players", "gauge", "Players (pieces) in all rooms", players);
        Metric("blob_tick_avg_ms", "gauge", "Average game-loop iteration time over the last ~10s (budget 33ms)", r.TickAvgMs);
        Metric("blob_tick_p99_ms", "gauge", "99th percentile game-loop iteration time over the last ~10s", r.TickP99Ms);
        Metric("blob_tick_max_ms", "gauge", "Slowest game-loop iteration over the last ~10s", r.TickMaxMs);
        Metric("blob_slow_ticks_total", "counter", "Iterations that exceeded the 33ms budget", r.SlowTicks);
        Metric("blob_snapshot_avg_bytes", "gauge", "Average snapshot size", r.SnapshotAvgBytes);
        Metric("blob_snapshot_max_bytes", "gauge", "Largest snapshot in the window (must stay under ~1400)", r.SnapshotMaxBytes);
        Metric("blob_packets_in_total", "counter", "UDP packets received", r.PacketsIn);
        Metric("blob_packets_rejected_total", "counter", "Packets dropped by the abuse guard", r.PacketsRejected);
        Metric("blob_packets_out_total", "counter", "UDP packets sent", r.PacketsOut);
        Metric("blob_bytes_out_total", "counter", "UDP bytes sent", r.BytesOut);
        Metric("blob_joins_total", "counter", "Accepted joins", r.Joins);
        Metric("blob_joins_rejected_total", "counter", "Joins refused by the abuse guard", r.JoinsRejected);
        Metric("blob_deaths_total", "counter", "Player deaths", r.Deaths);
        return sb.ToString();
    }
}
