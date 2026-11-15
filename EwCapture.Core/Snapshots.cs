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

    /// <summary>What identifies the tab: a newer read of the same key replaces one still waiting.</summary>
    public string Key => $"{Tab.Layout}|{Tab.Name}";
}

/// <summary>
/// Snapshots waiting on disk until the server has them, oldest first. One file per tab: the
/// user wants to know what the tab holds now, so a newer read of a tab replaces the one still
/// waiting. A site outage then costs one upload per tab afterwards, not one per read.
/// <para>
/// Reads the server refuses are set aside, never deleted on the spot: in rejected/ for good (a read
/// refused on its own, an unreadable file, a read an old version left), or in rejected/whole when the
/// whole request was refused (a 400), which is more likely the server's fault: those go back in line
/// once, after the next sync the server stores. Set-aside reads are deleted after <see cref="KeepSetAside"/>.
/// </para>
/// </summary>
public sealed class SnapshotQueue(string dir)
{
    static long _sequence;
    public string Directory { get; } = dir;
    public string RejectedDirectory => Path.Combine(Directory, "rejected");
    /// <summary>Reads a whole-body refusal set aside, waiting for their one more try.</summary>
    public string WholeRejectedDirectory => Path.Combine(RejectedDirectory, "whole");

    /// <summary>How long a set-aside read is kept (to look at, or for its retry) before start-up deletes it.</summary>
    public static readonly TimeSpan KeepSetAside = TimeSpan.FromDays(30);

    // Reads put back in line after a whole-body refusal this session: refused whole again, they're set aside for good.
    readonly HashSet<string> _retried = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads a scanner older than this left queued are set aside at start-up, never sent (see
    /// <see cref="SetAsideOlderThan"/>). Keep it equal to the site's MIN_UPLOAD_VERSION (worker
    /// lib/stash.ts): the site refuses uploads from older scanners, so their reads mustn't reach it
    /// through a newer one either. Raising one without the other lets those reads through.
    /// </summary>
    public static readonly Version MinQueuedReadVersion = new(0, 5, 0);

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
    /// Everything waiting, in one request per 50 reads: the server answers each read on its own. A stored
    /// read is deleted, a refused one set aside for good; a refused request (400) sets its reads aside for
    /// one more try (see the class notes); on a failure that isn't the reads' fault they all wait for the
    /// next try. Once a request is stored, reads waiting for their one more try go back in line, for the
    /// caller's next drain. Returns how many reads were stored, how the last request went, and how long
    /// the server asked us to wait.
    /// </summary>
    public async Task<(int Sent, UploadOutcome Outcome, TimeSpan? RetryAfter)> SyncAsync(ScannerApi api, CancellationToken ct = default)
    {
        int sent = 0;
        while (true)
        {
            var ids = Pending().Take(MaxReadsPerSync).ToList();
            if (ids.Count == 0) return (sent, UploadOutcome.Stored, null);
            var reads = new List<(string Id, Snapshot Snap)>();
            foreach (var id in ids)
            {
                try { reads.Add((id, JsonSerializer.Deserialize<Snapshot>(await File.ReadAllTextAsync(Path.Combine(Directory, id + ".json"), ct), CaptureMeta.Json)!)); }
                catch (FileNotFoundException) { } // replaced by a newer read of the same tab meanwhile
                catch (JsonException) { SetAside(id); } // a file we can't read back will never upload
            }
            if (reads.Count == 0) continue; // every one was replaced or unreadable: look again, nothing to send for these
            var body = JsonSerializer.Serialize(new
            {
                reads = reads.Select(r => r.Snap.ToSyncRead()),
                // Seen notes went with the 2 s timer; the field stays, empty, so every site version parses the body alike.
                seen = Array.Empty<object>(),
            }, CaptureMeta.Json);
            var res = await api.SyncAsync(body, ct);
            if (res.Outcome == UploadOutcome.Rejected)
            {
                // The whole body was refused: one more try for reads that haven't had it this session.
                foreach (var r in reads) SetAside(r.Id, forRetry: !_retried.Contains(r.Id));
                return (sent, res.Outcome, null);
            }
            if (res.Outcome != UploadOutcome.Stored) return (sent, res.Outcome, res.RetryAfter);
            for (int i = 0; i < reads.Count; i++)
            {
                var error = i < res.ReadErrors.Count ? res.ReadErrors[i] : "no answer";
                if (error is null) { File.Delete(Path.Combine(Directory, reads[i].Id + ".json")); sent++; }
                else SetAside(reads[i].Id); // refused on its own: the server looked at it, so no retry
            }
            RequeueForRetry();
            if (ids.Count < MaxReadsPerSync) return (sent, UploadOutcome.Stored, null);
        }
    }

    /// <summary>
    /// The server stores again: reads a whole-body refusal set aside go back in line, each once per session.
    /// One whose tab has a newer read waiting is deleted instead, as <see cref="Enqueue"/> would have.
    /// </summary>
    int RequeueForRetry()
    {
        if (!System.IO.Directory.Exists(WholeRejectedDirectory)) return 0;
        int n = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(WholeRejectedDirectory, "*.json").ToList())
        {
            var id = Path.GetFileNameWithoutExtension(file);
            _retried.Add(id);
            var tab = id[(id.LastIndexOf('-') + 1)..];
            if (System.IO.Directory.EnumerateFiles(Directory, $"*-{tab}.json").Any()) { File.Delete(file); continue; }
            File.Move(file, Path.Combine(Directory, id + ".json"), overwrite: true);
            n++;
        }
        return n;
    }

    /// <summary>How many reads are set aside, for good or for their one more try (the tray says so while there are any).</summary>
    public int SetAsideCount() => CountJson(RejectedDirectory) + CountJson(WholeRejectedDirectory);

    static int CountJson(string dir) => System.IO.Directory.Exists(dir) ? System.IO.Directory.EnumerateFiles(dir, "*.json").Count() : 0;

    /// <summary>
    /// Deletes set-aside reads (both folders) whose file was written more than <paramref name="maxAge"/>
    /// before <paramref name="nowUtc"/>; a move keeps the time, so that's when the read was taken. Returns how many.
    /// </summary>
    public int DeleteSetAsideOlderThan(TimeSpan maxAge, DateTime nowUtc)
    {
        int n = 0;
        foreach (var dir in new[] { RejectedDirectory, WholeRejectedDirectory })
        {
            if (!System.IO.Directory.Exists(dir)) continue;
            foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.json").ToList())
            {
                if (nowUtc - File.GetLastWriteTimeUtc(file) <= maxAge) continue;
                File.Delete(file);
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// Sets aside waiting reads taken by a scanner older than <paramref name="min"/> (Tool is
    /// "ew-capture X.Y.Z"). Up to 0.4.13 the scanner read the screen on a timer, which GGG's rules
    /// don't allow, and the site refuses those versions; their reads aren't sent after an update either.
    /// </summary>
    public int SetAsideOlderThan(Version min)
    {
        int n = 0;
        foreach (var id in Pending())
        {
            try
            {
                var s = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(Path.Combine(Directory, id + ".json")), CaptureMeta.Json);
                if (s?.Tool?.Split(' ').LastOrDefault() is { } v && Version.TryParse(v, out var taken) && taken >= min) continue;
            }
            catch (FileNotFoundException) { continue; }
            catch (JsonException) { } // unreadable: set aside, as SyncAsync would
            SetAside(id);
            n++;
        }
        return n;
    }

    void SetAside(string id, bool forRetry = false)
    {
        var file = Path.Combine(Directory, id + ".json");
        // A newer read of the same tab replaces the file (Enqueue) while its request is out: then
        // there's nothing left to set aside. Enqueue and SyncAsync share one thread, so this can't race.
        if (!File.Exists(file)) return;
        var dir = forRetry ? WholeRejectedDirectory : RejectedDirectory;
        System.IO.Directory.CreateDirectory(dir);
        File.Move(file, Path.Combine(dir, id + ".json"), overwrite: true);
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
