using System.IO;
using System.Text.Json;

namespace Snap.Services;

public class UsageTracker
{
    private static readonly string UsagePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snap", "usage.json");

    private const int MaxEntries = 10_000;

    private Dictionary<string, int> _usage = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public void Load()
    {
        try
        {
            if (!File.Exists(UsagePath))
                return;

            var json = File.ReadAllText(UsagePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (data != null)
            {
                _usage = new Dictionary<string, int>(data, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            _usage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save()
    {
        try
        {
            Dictionary<string, int> snapshot;
            lock (_lock)
            {
                // Trim to MaxEntries: keep the ones with highest counts
                if (_usage.Count > MaxEntries)
                {
                    var trimmed = _usage
                        .OrderByDescending(kv => kv.Value)
                        .Take(MaxEntries)
                        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                    _usage = trimmed;
                }
                snapshot = new Dictionary<string, int>(_usage);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(UsagePath)!);
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(UsagePath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }

    public void RecordAccess(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        lock (_lock)
        {
            _usage.TryGetValue(path, out var count);
            _usage[path] = count + 1;
        }
    }

    public int GetAccessCount(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return 0;

        lock (_lock)
        {
            return _usage.TryGetValue(path, out var count) ? count : 0;
        }
    }

    public int GetFrequencyLevel(string path)
    {
        var count = GetAccessCount(path);
        return count switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            <= 4 => 3,
            <= 7 => 4,
            <= 12 => 5,
            <= 20 => 6,
            <= 35 => 7,
            <= 60 => 8,
            <= 100 => 9,
            _ => 10,
        };
    }
}
