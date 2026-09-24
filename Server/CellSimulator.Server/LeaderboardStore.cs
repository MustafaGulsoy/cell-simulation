using System.Text.Json;

namespace CellSimulator.Server;

/// <summary>
/// The all-time / weekly / daily best runs, kept across restarts in one small JSON file (no
/// database: the whole thing is a few hundred entries). Every finished life above
/// <see cref="MinRecordedMass"/> is added; queries return each player name's single best entry for
/// the period. Writes are atomic (temp file + move) so a crash mid-save can't corrupt it, and a
/// damaged or missing file just starts an empty board instead of stopping the server.
/// </summary>
public sealed class LeaderboardStore
{
    public const float MinRecordedMass = 100f;
    private const int MaxEntries = 5000;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    public sealed record Entry(string Name, float Mass, float SurvivedSeconds, DateTime Utc);

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly List<Entry> _entries = new();

    /// <param name="path">JSON file to persist to; null keeps everything in memory (tests).</param>
    public LeaderboardStore(string? path = null)
    {
        _path = path;
        if (path == null || !File.Exists(path)) return;

        try
        {
            var loaded = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path));
            if (loaded != null) _entries.AddRange(loaded.Where(e => !string.IsNullOrWhiteSpace(e.Name) && float.IsFinite(e.Mass)));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable: keep the bad file for inspection and start fresh rather than refuse to boot.
            try { File.Move(path, path + ".corrupt", overwrite: true); } catch (IOException) { }
        }
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Adds a finished run (ignored below MinRecordedMass or with a non-finite mass).</summary>
    public void Record(string name, float peakMass, float survivedSeconds, DateTime? utc = null)
    {
        if (!float.IsFinite(peakMass) || peakMass < MinRecordedMass || string.IsNullOrWhiteSpace(name)) return;

        lock (_gate)
        {
            var now = utc ?? DateTime.UtcNow;
            _entries.Add(new Entry(name, peakMass, survivedSeconds, now));

            // Bounded: drop anything old, then keep only the best MaxEntries.
            _entries.RemoveAll(e => now - e.Utc > MaxAge);
            if (_entries.Count > MaxEntries)
            {
                var best = _entries.OrderByDescending(e => e.Mass).Take(MaxEntries).ToList();
                _entries.Clear();
                _entries.AddRange(best);
            }

            Save();
        }
    }

    /// <summary>Best entry per player name within the last <paramref name="window"/> (null = ever), highest mass first.</summary>
    public List<Entry> Top(TimeSpan? window, int limit)
    {
        lock (_gate)
        {
            var since = window.HasValue ? DateTime.UtcNow - window.Value : DateTime.MinValue;
            return _entries
                .Where(e => e.Utc >= since)
                .GroupBy(e => e.Name)
                .Select(g => g.OrderByDescending(e => e.Mass).First())
                .OrderByDescending(e => e.Mass)
                .Take(Math.Clamp(limit, 1, 100))
                .ToList();
        }
    }

    private void Save()
    {
        if (_path == null) return;

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disk full / read-only volume: the board still works in memory, it just won't survive a restart.
        }
    }
}
