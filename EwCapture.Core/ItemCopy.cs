using System.Text.Json;

namespace EwCapture.Core;

/// <summary>
/// A waystone or tablet as the game copies it: the text it puts on the clipboard when the player presses
/// Ctrl+C on one. It's the only place a map's waystone and tablet mods can be read; the site shows them in
/// the player's sessions. Opt-in (the tray's "Record waystone and tablet copies").
/// </summary>
public static class ItemCopy
{
    /// <summary>The site takes no more; real copies are well under 1,500 characters.</summary>
    public const int MaxLength = 4000;

    static readonly string[] Classes = ["Item Class: Waystones", "Item Class: Tablet", "Item Class: Waystone", "Item Class: Tablets"];

    /// <summary>
    /// The text to send when the clipboard holds a waystone or tablet copy (line ends made plain), else null.
    /// Whatever else was on the clipboard goes no further than this check: it's never kept, logged or sent.
    /// English clients only: the game writes the copy in the client's language.
    /// </summary>
    public static string? Accept(string? clipboard)
    {
        if (string.IsNullOrEmpty(clipboard) || clipboard.Length > MaxLength * 2) return null;
        var lines = clipboard.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count < 3 || !Classes.Contains(lines[0]) || !lines[1].StartsWith("Rarity: ", StringComparison.Ordinal)) return null;
        var text = string.Join('\n', lines);
        if (text.Length > MaxLength || text.Any(c => c != '\n' && char.IsControl(c))) return null;
        return text;
    }

    /// <summary>What an accepted copy is, for the tray and the log: "Waystone (Tier 15)", "Irradiated Tablet".</summary>
    public static string Label(string accepted)
    {
        var head = accepted.Split('\n').TakeWhile(l => l != "--------").ToList();
        return head.Count > 2 ? head[^1] : "item";
    }
}

/// <summary>Copies waiting on disk until the server has them, oldest first. One small file each.</summary>
public sealed class ItemCopyQueue(string dir)
{
    sealed record Entry(DateTimeOffset At, string Text);

    static long _sequence;
    public string Directory { get; } = dir;

    /// <summary>The server takes up to this many copies in one request.</summary>
    public const int MaxPerRequest = 25;
    /// <summary>A PC that can't reach the server keeps this many; older ones go first. A long day of maps is a few hundred.</summary>
    public const int MaxWaiting = 300;

    public void Enqueue(DateTimeOffset at, string text)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Interlocked.Increment(ref _sequence):D8}";
        var tmp = Path.Combine(Directory, id + ".json.tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Entry(at, text), CaptureMeta.Json));
        File.Move(tmp, Path.Combine(Directory, id + ".json"));
        foreach (var old in Pending().SkipLast(MaxWaiting)) File.Delete(Path.Combine(Directory, old + ".json"));
    }

    public IReadOnlyList<string> Pending() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory, "*.json").Select(p => Path.GetFileNameWithoutExtension(p)!).Order(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>Drops everything waiting: the player turned copies off, or the server doesn't take them.</summary>
    public int Clear()
    {
        var ids = Pending();
        foreach (var id in ids) File.Delete(Path.Combine(Directory, id + ".json"));
        return ids.Count;
    }

    /// <summary>
    /// Everything waiting, 25 copies a request. A request the server answered is done with, whatever it said
    /// of each copy (one it refuses is not a waystone or tablet to it, and never will be); so is one it
    /// refused whole. On a failure that isn't the copies' fault they wait for the next try. Returns how many
    /// were stored, how the last request went, how long the server asked us to wait, and whether it said it
    /// doesn't take item copies at all (then nothing is left waiting).
    /// </summary>
    public async Task<(int Sent, UploadOutcome Outcome, TimeSpan? RetryAfter, bool NotTaken)> SyncAsync(ScannerApi api, CancellationToken ct = default)
    {
        int sent = 0;
        while (true)
        {
            var ids = Pending().Take(MaxPerRequest).ToList();
            if (ids.Count == 0) return (sent, UploadOutcome.Stored, null, false);
            var items = new List<object>();
            foreach (var id in ids)
            {
                try
                {
                    var e = JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(Path.Combine(Directory, id + ".json"), ct), CaptureMeta.Json);
                    if (e?.Text is not null) items.Add(new { at = e.At, text = e.Text });
                }
                catch (FileNotFoundException) { }
                catch (JsonException) { } // unreadable: deleted with the batch below
            }
            if (items.Count > 0)
            {
                var res = await api.SendItemsAsync(JsonSerializer.Serialize(new { items }, CaptureMeta.Json), ct);
                if (res.Outcome is UploadOutcome.Unlinked or UploadOutcome.RetryLater) return (sent, res.Outcome, res.RetryAfter, false);
                sent += res.Stored;
                if (res.Outcome == UploadOutcome.Rejected)
                {
                    // The server won't have these (or, on a 404, any): nothing to gain by keeping them.
                    if (res.NotTaken) Clear(); else Delete(ids);
                    return (sent, res.Outcome, null, res.NotTaken);
                }
            }
            Delete(ids);
            if (ids.Count < MaxPerRequest) return (sent, UploadOutcome.Stored, null, false);
        }
    }

    void Delete(IEnumerable<string> ids)
    {
        foreach (var id in ids) File.Delete(Path.Combine(Directory, id + ".json"));
    }
}

/// <summary>
/// Whether a clipboard change is read at all, decided before the clipboard is opened. GGG's rule is one
/// read per manual key press, and the press here is the player's own Ctrl+C in the game: so only with the
/// option on, on a server that takes copies, with the game in front, when the clipboard was set by the game
/// (or by nobody Windows can name), and not twice for one copy (Windows may announce a change more than once).
/// </summary>
public sealed class ItemCopyGate
{
    /// <summary>Changes this close together are one copy.</summary>
    public static readonly TimeSpan OnePress = TimeSpan.FromMilliseconds(300);
    /// <summary>The same item again this soon is the same press (or a key held down), not a second item.</summary>
    public static readonly TimeSpan SameItem = TimeSpan.FromSeconds(2);

    DateTime _lastRead = DateTime.MinValue;
    (string Text, DateTime At) _lastKept = ("", DateTime.MinValue);

    /// <param name="ownerIsGame">True when the game set the clipboard, false when another program did, null when Windows names no owner.</param>
    public bool MayRead(bool enabled, bool serverTakesCopies, bool gameInFront, bool? ownerIsGame, DateTime now)
    {
        if (!enabled || !serverTakesCopies || !gameInFront || ownerIsGame == false) return false;
        if (now - _lastRead < OnePress) return false;
        _lastRead = now;
        return true;
    }

    /// <summary>The accepted text when it's a new copy, null when it repeats the one just kept.</summary>
    public string? Keep(string? accepted, DateTime now)
    {
        if (accepted is null) return null;
        if (accepted == _lastKept.Text && now - _lastKept.At < SameItem) return null;
        _lastKept = (accepted, now);
        return accepted;
    }
}
