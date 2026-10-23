using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EwCapture.Core.Recognition;

namespace EwCapture.Core;

public sealed record SnapshotTab(string Layout, string Name, int Index, double Score);
public sealed record SnapshotRead(int Slot, string Item, int Count, double Confidence);

/// <summary>One tab's read as POST /api/scanner/snapshots takes it: counts, never pixels.</summary>
public sealed record Snapshot(DateTimeOffset CapturedAt, string Tool, string Patch, SnapshotTab Tab, SnapshotRead[] Reads)
{
    public static Snapshot From(TabRead read, string tabName, string tool, string patch, DateTimeOffset capturedAt) => new(
        capturedAt, tool, patch,
        // The strip scrolls, so a tab's position in the stash isn't visible: copies are told apart by name.
        new SnapshotTab(read.Tab.Layout, tabName, 0, Math.Round(read.Tab.Score, 3)),
        read.Reads.Where(r => r.Item is not null)
            .Select(r => new SnapshotRead(r.Slot, r.Item!, r.Count, Math.Round(r.Confidence, 3)))
            .ToArray());

    public string ToJson() => JsonSerializer.Serialize(this, CaptureMeta.Json);

    /// <summary>What identifies the tab and its contents, for "did anything change".</summary>
    public string Key => $"{Tab.Layout}|{Tab.Name}";
    public string Contents => string.Join(",", Reads.Select(r => $"{r.Slot}:{r.Count}"));
}

/// <summary>
/// Decides which reads are worth sending: only a tab whose counts changed. The user scans
/// to learn what changed and by how much; an unchanged tab is reported through the seen
/// note instead (see <see cref="SeenTracker"/>), which the site keeps without storing a read.
/// </summary>
public sealed class ChangeTracker
{
    readonly Dictionary<string, (string Contents, DateTimeOffset At)> _sent = new();

    public bool ShouldSend(Snapshot s) =>
        !_sent.TryGetValue(s.Key, out var last) || last.Contents != s.Contents;

    public void MarkSent(Snapshot s) => _sent[s.Key] = (s.Contents, s.CapturedAt);
}

/// <summary>
/// Tabs that were on screen with unchanged counts, for POST /api/scanner/seen: lets a farming
/// session tell "checked, no change" from "not opened". At most one note per tab per
/// <paramref name="every"/>; best effort, kept in memory only.
/// </summary>
public sealed class SeenTracker(TimeSpan every)
{
    readonly Dictionary<string, DateTimeOffset> _noted = new();
    readonly Dictionary<string, (SnapshotTab Tab, DateTimeOffset At)> _pending = new();
    readonly object _lock = new();

    public void Note(Snapshot s)
    {
        lock (_lock)
        {
            if (_noted.TryGetValue(s.Key, out var last) && s.CapturedAt - last < every) return;
            _noted[s.Key] = s.CapturedAt;
            _pending[s.Key] = (s.Tab, s.CapturedAt);
        }
    }

    /// <summary>The notes not sent yet, as the request body; null when there are none. Taking them clears them.</summary>
    public string? Take()
    {
        lock (_lock)
        {
            if (_pending.Count == 0) return null;
            var tabs = _pending.Values.Take(50).Select(p => new { layout = p.Tab.Layout, name = p.Tab.Name, index = p.Tab.Index, at = p.At }).ToList();
            _pending.Clear();
            return JsonSerializer.Serialize(new { tabs }, CaptureMeta.Json);
        }
    }
}

/// <summary>
/// Snapshots waiting on disk until the server has them, oldest first. One file per tab: the
/// user wants to know what the tab holds now, so a newer read of a tab replaces the one still
/// waiting. A site outage then costs one upload per tab afterwards, not one per read.
/// </summary>
public sealed class SnapshotQueue(string dir)
{
    static long _sequence;
    public string Directory { get; } = dir;
    public string RejectedDirectory => Path.Combine(Directory, "rejected");

    public string Enqueue(Snapshot s)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var tab = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s.Key)))[..8];
        foreach (var old in System.IO.Directory.EnumerateFiles(Directory, $"*-{tab}.json")) File.Delete(old);
        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Interlocked.Increment(ref _sequence):D8}-{tab}";
        var tmp = Path.Combine(Directory, id + ".json.tmp");
        File.WriteAllText(tmp, s.ToJson());
        File.Move(tmp, Path.Combine(Directory, id + ".json"));
        return id;
    }

    public IReadOnlyList<string> Pending() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory, "*.json").Select(p => Path.GetFileNameWithoutExtension(p)!).Order(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>Upload in order; stop at the first failure that isn't the snapshot's own fault.</summary>
    public async Task<int> DrainAsync(ScannerApi api, CancellationToken ct = default)
    {
        int sent = 0;
        foreach (var id in Pending())
        {
            var file = Path.Combine(Directory, id + ".json");
            string body;
            try { body = await File.ReadAllTextAsync(file, ct); }
            catch (FileNotFoundException) { continue; } // replaced by a newer read of the same tab meanwhile
            var outcome = await api.UploadSnapshotAsync(body, ct);
            if (outcome == UploadOutcome.Stored) { File.Delete(file); sent++; continue; }
            if (outcome == UploadOutcome.Rejected)
            {
                System.IO.Directory.CreateDirectory(RejectedDirectory);
                File.Move(file, Path.Combine(RejectedDirectory, id + ".json"), overwrite: true);
                continue;
            }
            break;
        }
        return sent;
    }
}

/// <summary>
/// Picks the name a read is filed under. A name read off the strip is trusted only
/// when nothing covers the strip; under a tooltip, the one name known for that tab
/// type is reused, and with several copies known the read waits (null) rather than
/// risk filing it under the wrong copy.
/// </summary>
public sealed class TabNamer
{
    readonly Dictionary<string, HashSet<string>> _known = new();

    public string? Choose(string layout, string fallbackLabel, bool stripUncovered, string? ocrName)
    {
        if (stripUncovered)
        {
            var name = ocrName is { Length: >= 2 } ? ocrName : fallbackLabel;
            (_known.TryGetValue(layout, out var set) ? set : _known[layout] = []).Add(name);
            return name;
        }
        return _known.TryGetValue(layout, out var names) && names.Count == 1 ? names.First() : null;
    }
}
