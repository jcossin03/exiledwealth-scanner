namespace EwCapture.Core;

/// <summary>
/// Captures wait on disk until the server has them, so a crash, a closed laptop
/// lid or a dead connection loses nothing. One capture = id.png + id.json.
/// </summary>
public sealed class UploadQueue(string dir)
{
    static long _sequence;

    public string Directory { get; } = dir;

    public string Enqueue(byte[] png, string metaJson)
    {
        System.IO.Directory.CreateDirectory(Directory);
        // Sortable by time; the counter keeps two captures in the same millisecond in order
        // (a later read of a tab must never upload before an earlier one).
        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Interlocked.Increment(ref _sequence):D8}-{Guid.NewGuid().ToString("N")[..6]}";
        File.WriteAllText(Path.Combine(Directory, id + ".json"), metaJson);
        // The PNG is written last and is what marks the capture complete.
        var tmp = Path.Combine(Directory, id + ".png.tmp");
        File.WriteAllBytes(tmp, png);
        File.Move(tmp, Path.Combine(Directory, id + ".png"));
        return id;
    }

    /// <summary>Complete captures, oldest first.</summary>
    public IReadOnlyList<string> Pending()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return System.IO.Directory.EnumerateFiles(Directory, "*.png")
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Where(id => File.Exists(Path.Combine(Directory, id + ".json")))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public (byte[] Png, string MetaJson) Read(string id) =>
        (File.ReadAllBytes(Path.Combine(Directory, id + ".png")),
         File.ReadAllText(Path.Combine(Directory, id + ".json")));

    public string RejectedDirectory => Path.Combine(Directory, "rejected");

    /// <summary>Set aside a capture the server refused, so it can still be inspected.</summary>
    public void Reject(string id)
    {
        System.IO.Directory.CreateDirectory(RejectedDirectory);
        foreach (var ext in new[] { ".png", ".json" })
            File.Move(Path.Combine(Directory, id + ext), Path.Combine(RejectedDirectory, id + ext), overwrite: true);
    }

    /// <summary>Put rejected captures back in line, e.g. after a client fix. Returns how many.</summary>
    public int RequeueRejected()
    {
        if (!System.IO.Directory.Exists(RejectedDirectory)) return 0;
        var ids = System.IO.Directory.EnumerateFiles(RejectedDirectory, "*.png")
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Where(id => File.Exists(Path.Combine(RejectedDirectory, id + ".json")))
            .ToList();
        foreach (var id in ids)
            foreach (var ext in new[] { ".json", ".png" }) // png last: it marks the capture complete
                File.Move(Path.Combine(RejectedDirectory, id + ext), Path.Combine(Directory, id + ext), overwrite: true);
        return ids.Count;
    }

    public void Remove(string id)
    {
        File.Delete(Path.Combine(Directory, id + ".png"));
        File.Delete(Path.Combine(Directory, id + ".json"));
    }

    /// <summary>
    /// Upload everything pending. Stops at the first failure that isn't the
    /// capture's own fault, so a dead connection doesn't churn the whole queue.
    /// </summary>
    public async Task<int> DrainAsync(ScannerApi api, CancellationToken ct = default)
    {
        var sent = 0;
        foreach (var id in Pending())
        {
            var (png, meta) = Read(id);
            var outcome = await api.UploadCaptureAsync(png, meta, ct);
            if (outcome == UploadOutcome.Stored) { Remove(id); sent++; continue; }
            // The server will never take this file; retrying won't help, but keep it to look at.
            if (outcome == UploadOutcome.Rejected) { Reject(id); continue; }
            break;
        }
        return sent;
    }
}
