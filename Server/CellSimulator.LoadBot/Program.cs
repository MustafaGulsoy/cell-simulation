using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

// Load / soak test for the game server: N fake players speaking the real protocol (version 2, compact
// snapshots, parsed with the game's own SnapshotDecoder). Every bot joins, wanders, splits, ejects and
// pings, and the run reports what a real crowd would see: snapshot rate, packet sizes (must stay under one
// network packet), food delivery, latency, and the server's own tick time from /metrics.
//
//   dotnet run --project CellSimulator.LoadBot -- <host> [bots=20] [seconds=20] [udpPort=7778] [httpPort=0]
//
// Exit code 1 if the run shows a problem (packets over the MTU budget, starved snapshot rate, missing food).

const int MtuBudget = 1400;

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int botCount = args.Length > 1 ? int.Parse(args[1]) : 20;
int seconds = args.Length > 2 ? int.Parse(args[2]) : 20;
int udpPort = args.Length > 3 ? int.Parse(args[3]) : 7778;
int httpPort = args.Length > 4 ? int.Parse(args[4]) : 0;

var server = new IPEndPoint((await Dns.GetHostAddressesAsync(host)).First(a => a.AddressFamily == AddressFamily.InterNetwork), udpPort);
Console.WriteLine($"Load test: {botCount} bots -> {server} for {seconds}s");

var stats = new BotStats[botCount];
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
var tasks = Enumerable.Range(0, botCount).Select(i => Task.Run(() => RunBot(i, server, stats[i] = new BotStats(), cts.Token))).ToArray();
await Task.WhenAll(tasks);

// ---- report -----------------------------------------------------------------------------------------------
var joined = stats.Where(s => s.Joined).ToArray();
double avgHz = joined.Length == 0 ? 0 : joined.Average(s => s.Snapshots / (double)seconds);
int maxBytes = joined.Length == 0 ? 0 : joined.Max(s => s.MaxSnapshotBytes);
double avgBytes = joined.Sum(s => s.SnapshotBytes) / Math.Max(1.0, joined.Sum(s => s.Snapshots));
double avgRtt = joined.Where(s => s.Pongs > 0).Select(s => s.RttMsTotal / s.Pongs).DefaultIfEmpty(0).Average();
double maxRtt = joined.Select(s => s.MaxRttMs).DefaultIfEmpty(0).Max();
long foodTotal = joined.Sum(s => s.FoodReceived);
int fullFood = joined.Count(s => s.FoodReceived >= s.ExpectedFood && s.ExpectedFood > 0);

Console.WriteLine();
Console.WriteLine($"joined                : {joined.Length}/{botCount}");
Console.WriteLine($"snapshots / bot / sec : {avgHz:F1}   (server sends 30)");
Console.WriteLine($"snapshot bytes        : avg {avgBytes:F0}, max {maxBytes}   (budget {MtuBudget})");
Console.WriteLine($"ping ms               : avg {avgRtt:F1}, max {maxRtt:F1}");
Console.WriteLine($"food on join          : {fullFood}/{joined.Length} bots received the whole map's food ({foodTotal} pellets total)");
Console.WriteLine($"deaths seen           : {joined.Sum(s => s.Deaths)}   power-ups seen: {joined.Sum(s => s.PowerupsSeen)}");
Console.WriteLine($"decode errors         : {joined.Sum(s => s.DecodeErrors)}");

if (httpPort > 0)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var metrics = await http.GetStringAsync($"http://{host}:{httpPort}/metrics");
        Console.WriteLine("\nserver metrics:");
        foreach (var line in metrics.Split('\n').Where(l => l.StartsWith("blob_tick") || l.StartsWith("blob_snapshot") || l.StartsWith("blob_rooms") || l.StartsWith("blob_players")))
            Console.WriteLine("  " + line);
    }
    catch (Exception ex) { Console.WriteLine($"(metrics unavailable: {ex.Message})"); }
}

var problems = new List<string>();
if (joined.Length < botCount) problems.Add($"only {joined.Length}/{botCount} bots could join (join limits or capacity)");
if (maxBytes > MtuBudget) problems.Add($"snapshot of {maxBytes} bytes exceeds the {MtuBudget}-byte budget");
if (joined.Length > 0 && avgHz < 25) problems.Add($"snapshot rate {avgHz:F1}/s is far below 30");
if (joined.Length > 0 && fullFood < joined.Length) problems.Add($"{joined.Length - fullFood} bots did not receive all food");
if (joined.Sum(s => s.DecodeErrors) > 0) problems.Add("some snapshots failed to decode");

Console.WriteLine(problems.Count == 0 ? "\nOK" : "\nPROBLEMS:\n  - " + string.Join("\n  - ", problems));
return problems.Count == 0 ? 0 : 1;

// ------------------------------------------------------------------------------------------------------------------

static async Task RunBot(int index, IPEndPoint server, BotStats st, CancellationToken stop)
{
    using var udp = new UdpClient(0);
    udp.Client.ReceiveBufferSize = 1 << 20;
    var rng = new Random(index * 7919 + 13);
    var decoder = new SnapshotDecoder();

    // Join (retrying like the real client), announcing version 2 and a colour.
    var name = "lb" + index;
    var join = new byte[] { 1, (byte)name.Length }.Concat(System.Text.Encoding.UTF8.GetBytes(name)).Concat(new byte[] { 3, 2, (byte)rng.Next(30, 230), (byte)rng.Next(30, 230), (byte)rng.Next(30, 230) }).ToArray();
    uint myId = 0;
    var joinDeadline = DateTime.UtcNow.AddSeconds(6);
    while (myId == 0 && DateTime.UtcNow < joinDeadline && !stop.IsCancellationRequested)
    {
        await udp.SendAsync(join, join.Length, server);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
        wait.CancelAfter(1000);
        try
        {
            while (myId == 0)
            {
                var d = (await udp.ReceiveAsync(wait.Token)).Buffer;
                if (d[0] != 1) continue;
                myId = BitConverter.ToUInt32(d, 1);
                decoder.HalfWidth = BitConverter.ToInt32(d, 5);
                decoder.HalfHeight = d.Length >= 13 ? BitConverter.ToInt32(d, 9) : decoder.HalfWidth;
            }
        }
        catch (OperationCanceledException) { }
    }
    if (myId == 0) return;
    st.Joined = true;

    var sw = Stopwatch.StartNew();
    double nextInput = 0, nextAction = 2 + rng.NextDouble() * 3, nextPing = 1;
    float ax = 1, ay = 0;
    var foodSeen = new HashSet<uint>();

    while (!stop.IsCancellationRequested)
    {
        double now = sw.Elapsed.TotalSeconds;
        if (now >= nextInput)
        {
            nextInput = now + 0.05; // 20 Hz is plenty for a bot
            if (rng.NextDouble() < 0.02) { double a = rng.NextDouble() * Math.PI * 2; ax = (float)Math.Cos(a); ay = (float)Math.Sin(a); }
            var input = new byte[9]; input[0] = 2;
            BitConverter.GetBytes(ax).CopyTo(input, 1); BitConverter.GetBytes(ay).CopyTo(input, 5);
            await udp.SendAsync(input, input.Length, server);
        }
        if (now >= nextAction)
        {
            nextAction = now + 4 + rng.NextDouble() * 6;
            await udp.SendAsync(new byte[] { (byte)(rng.Next(3) == 0 ? 4 : 3) }, 1, server); // eject or split
        }
        if (now >= nextPing)
        {
            nextPing = now + 2;
            var ping = new byte[5]; ping[0] = 6;
            BitConverter.GetBytes((uint)(Environment.TickCount64 & 0xFFFFFFFF)).CopyTo(ping, 1);
            await udp.SendAsync(ping, ping.Length, server);
        }

        while (udp.Available > 0)
        {
            var d = (await udp.ReceiveAsync()).Buffer;
            switch (d[0])
            {
                case 3: // FoodFull chunk
                    {
                        int n = BitConverter.ToUInt16(d, 1);
                        for (int i = 0; i < n; i++)
                        {
                            uint id = BitConverter.ToUInt32(d, 3 + i * 12);
                            foodSeen.Add(id);
                            st.ExpectedFood = Math.Max(st.ExpectedFood, (int)id); // food ids are 1..FoodCount, so the highest seen is the total
                        }
                        st.FoodReceived = foodSeen.Count;
                        break;
                    }
                case 5: st.Deaths++; break;
                case 7:
                    {
                        double rtt = (uint)(Environment.TickCount64 & 0xFFFFFFFF) - BitConverter.ToUInt32(d, 1);
                        st.Pongs++; st.RttMsTotal += rtt; st.MaxRttMs = Math.Max(st.MaxRttMs, rtt);
                        break;
                    }
                default:
                    if (!SnapshotDecoder.IsSnapshot(d[0])) break;
                    try
                    {
                        var snap = decoder.Decode(d);
                        st.Snapshots++; st.SnapshotBytes += d.Length; st.MaxSnapshotBytes = Math.Max(st.MaxSnapshotBytes, d.Length);
                        st.PowerupsSeen += snap.Entities.Count(e => e.Type == 4);
                        foreach (var f in snap.Food) foodSeen.Add(f.Id);   // the rolling refresh delivers pellets the join dump missed
                        st.FoodReceived = foodSeen.Count;
                    }
                    catch (EndOfStreamException) { st.DecodeErrors++; }
                    break;
            }
        }
        await Task.Delay(5);
    }
}

sealed class BotStats
{
    public bool Joined;
    public int Snapshots, MaxSnapshotBytes, Deaths, PowerupsSeen, DecodeErrors, Pongs;
    public long SnapshotBytes;
    public int FoodReceived, ExpectedFood;
    public double RttMsTotal, MaxRttMs;
}
