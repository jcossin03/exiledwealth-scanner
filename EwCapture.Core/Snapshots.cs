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

    /// <summary>The read as POST /api/scanner/sync takes it: counts only, the item being implied by the slot.</summary>
    public object ToSyncRead() => new { layout = Tab.Layout, name = Tab.Name, index = Tab.Index, at = CapturedAt, counts = Reads.Select(r => new[] { r.Slot, r.Count }).ToArray() };

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

    /// <summary>The notes not sent yet (at most 50); taking them clears them.</summary>
    public List<SeenNote> TakeNotes()
    {
        lock (_lock)
        {
            var notes = _pending.Values.Take(50).Select(p => new SeenNote(p.Tab.Layout, p.Tab.Name, p.Tab.Index, p.At)).ToList();
            _pending.Clear();
            return notes;
        }
    }
}

/// <summary>A tab that was on screen with unchanged counts, for the sync body.</summary>
public sealed record SeenNote(string Layout, string Name, int Index, DateTimeOffset At);

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

    /// <summary>The server takes up to this many reads in one request.</summary>
    public const int MaxReadsPerSync = 50;

    /// <summary>
    /// Everything waiting, in one request per 50 reads, with the seen notes on the first: the server
    /// answers each read on its own. A stored read is deleted, a refused one set aside to look at;
    /// on a failure that isn't the reads' fault they all wait for the next try. Returns how many
    /// reads were stored, how the last request went, and how long the server asked us to wait.
    /// </summary>
    public async Task<(int Sent, UploadOutcome Outcome, TimeSpan? RetryAfter)> SyncAsync(ScannerApi api, IReadOnlyList<SeenNote> seen, CancellationToken ct = default)
    {
        int sent = 0;
        var notes = seen;
        while (true)
        {
            var ids = Pending().Take(MaxReadsPerSync).ToList();
            if (ids.Count == 0 && notes.Count == 0) return (sent, UploadOutcome.Stored, null);
            var reads = new List<(string Id, Snapshot Snap)>();
            foreach (var id in ids)
            {
                try { reads.Add((id, JsonSerializer.Deserialize<Snapshot>(await File.ReadAllTextAsync(Path.Combine(Directory, id + ".json"), ct), CaptureMeta.Json)!)); }
                catch (FileNotFoundException) { } // replaced by a newer read of the same tab meanwhile
                catch (JsonException) { SetAside(id); } // a file we can't read back will never upload
            }
            var body = JsonSerializer.Serialize(new
            {
                reads = reads.Select(r => r.Snap.ToSyncRead()),
                seen = notes.Select(n => new { layout = n.Layout, name = n.Name, index = n.Index, at = n.At }),
            }, CaptureMeta.Json);
            var res = await api.SyncAsync(body, ct);
            notes = []; // sent, or lost: a note only tells a session the tab was checked
            if (res.Outcome == UploadOutcome.Rejected) { foreach (var r in reads) SetAside(r.Id); } // the whole body was refused
            if (res.Outcome != UploadOutcome.Stored) return (sent, res.Outcome, res.RetryAfter);
            for (int i = 0; i < reads.Count; i++)
            {
                var error = i < res.ReadErrors.Count ? res.ReadErrors[i] : "no answer";
                if (error is null) { File.Delete(Path.Combine(Directory, reads[i].Id + ".json")); sent++; }
                else SetAside(reads[i].Id);
            }
            if (ids.Count < MaxReadsPerSync) return (sent, UploadOutcome.Stored, null);
        }
    }

    void SetAside(string id)
    {
        System.IO.Directory.CreateDirectory(RejectedDirectory);
        File.Move(Path.Combine(Directory, id + ".json"), Path.Combine(RejectedDirectory, id + ".json"), overwrite: true);
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
