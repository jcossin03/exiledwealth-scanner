using EwCapture.Core;
using EwCapture.Core.Recognition;

namespace EwCapture;

/// <summary>
/// Reads special tabs as the player opens them. Every couple of seconds, while the
/// game is in front, it checks a thin strip of the screen for the open stash, and
/// only then reads the panel; when a known tab's counts changed, it queues a
/// snapshot. It never presses keys or clicks.
/// </summary>
sealed class AutoReader : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan Heartbeat = TimeSpan.FromHours(6);

    readonly SnapshotQueue _queue;
    readonly IconCache _icons;
    readonly Action<string> _log;
    readonly Action<string> _status;
    readonly Action _queued;
    readonly ChangeTracker _tracker = new(Heartbeat);
    readonly TabNamer _namer = new();
    readonly System.Threading.Timer _timer;
    readonly CountReader _counts = CountReader.FromEmbedded();
    LayoutSet? _layouts;
    StashReader? _reader;
    int _busy;
    ulong _lastFrameHash;

    public bool Enabled { get; set; } = true;

    /// <summary>Tabs read with unchanged counts, sent by the app's upload pass.</summary>
    public SeenTracker Seen { get; } = new(TimeSpan.FromMinutes(2));

    /// <param name="status">Shown in the tray menu (called off the UI thread).</param>
    /// <param name="queued">A snapshot was queued: time to upload (called off the UI thread).</param>
    public AutoReader(SnapshotQueue queue, IconCache icons, Action<string> log, Action<string> status, Action queued)
    {
        (_queue, _icons, _log, _status, _queued) = (queue, icons, log, status, queued);
        _timer = new System.Threading.Timer(_ => _ = TickAsync(force: false), null, Interval, Interval);
    }

    public void UseLayouts(LayoutSet layouts)
    {
        _layouts = layouts;
        _reader = new StashReader(layouts, _icons, _counts);
        _lastFrameHash = 0;
    }

    /// <summary>Read the screen now (the hotkey), sending even if nothing changed.</summary>
    public Task ReadNowAsync() => TickAsync(force: true);

    async Task TickAsync(bool force)
    {
        if ((!Enabled && !force) || _reader is null || _layouts is null) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return; // the previous read is still running
        try
        {
            var (window, problem) = GameWindow.Find(onlyIfInFront: !force);
            if (window is null) { if (force) _status(problem!); return; }
            // Stash closed: stop after a thin strip of pixels. That's almost every tick while playing.
            if (!TabStrip.StashOpen(GameWindow.GrabGateStrip(window)))
            {
                _lastFrameHash = 0;
                if (force) _status("The stash isn't open");
                return;
            }
            var frame = GameWindow.Grab(window, panelOnly: true).Frame.NormalisedTo1440();

            // Nothing moved since the last look: skip the full read.
            var hash = Fingerprint(frame);
            if (!force && hash == _lastFrameHash) return;
            _lastFrameHash = hash;

            var read = _reader.Read(frame);
            if (read is null) { if (force) _status("No special tab recognised on screen"); return; }
            var label = _layouts.Layouts[read.Tab.Layout].Label;
            var uncovered = TabStrip.StripUncovered(frame);
            var name = _namer.Choose(read.Tab.Layout, label, uncovered, uncovered ? await TabNameReader.ReadAsync(frame) : null);
            if (name is null)
            {
                // A tooltip covers the tab name: read again once it's gone.
                _lastFrameHash = 0;
                if (force) _status("A tooltip covers the tab name: move the mouse off the tab buttons");
                return;
            }
            var snap = Snapshot.From(read, name, App.ToolVersion, _layouts.Patch, DateTimeOffset.UtcNow);
            if (!force && !_tracker.ShouldSend(snap)) { Seen.Note(snap); _queued(); return; }

            _queue.Enqueue(snap);
            _tracker.MarkSent(snap);
            var summary = $"{label} \"{name}\": {snap.Reads.Length} stacks";
            _log($"read {summary} (score {read.Tab.Score:F2})");
            _status($"{DateTime.Now:HH:mm} {summary}");
            _queued();
        }
        catch (Exception e) { _log($"auto read failed: {e}"); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>A coarse hash of the stash panel and tab strip: equal means nothing visible changed.</summary>
    static ulong Fingerprint(Frame f)
    {
        ulong h = 1469598103934665603UL;
        for (int y = 120; y < Math.Min(f.Height, 1300); y += 12)
            for (int x = 0; x < Math.Min(f.Width, 900); x += 12)
            {
                int p = (y * f.Width + x) * 4;
                int g = (f.Bgra[p] + f.Bgra[p + 1] * 2 + f.Bgra[p + 2]) >> 6; // quantised, so noise doesn't count
                h = (h ^ (ulong)g) * 1099511628211UL;
            }
        return h;
    }

    public void Dispose() => _timer.Dispose();
}
