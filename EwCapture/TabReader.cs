using EwCapture.Core;
using EwCapture.Core.Recognition;

namespace EwCapture;

/// <summary>
/// Reads the open special tab when the player presses the hotkey, from the one screen copy
/// that press made. GGG's developer docs (checked 2026-11-14): "Reading from the screen or
/// clipboard may only be performed once per manual key press." So there is no timer, and a
/// press is never turned into a second copy. It never presses keys or clicks.
/// </summary>
sealed class TabReader
{
    readonly SnapshotQueue _queue;
    readonly IconCache _icons;
    readonly Action<string> _log;
    readonly Action<string> _status;
    readonly Action _queued;
    readonly TabNamer _namer = new();
    readonly CountReader _counts = CountReader.FromEmbedded();
    LayoutSet? _layouts;
    StashReader? _reader;
    int _busy;

    /// <summary>
    /// Tabs opened without a change. Nothing notes them since reads became hotkey-only (every
    /// read is sent whole); kept so the sync request keeps its shape.
    /// </summary>
    public SeenTracker Seen { get; } = new(TimeSpan.FromMinutes(5));

    /// <param name="status">Shown in the tray menu (called off the UI thread).</param>
    /// <param name="queued">A snapshot was queued: time to upload (called off the UI thread).</param>
    public TabReader(SnapshotQueue queue, IconCache icons, Action<string> log, Action<string> status, Action queued) =>
        (_queue, _icons, _log, _status, _queued) = (queue, icons, log, status, queued);

    public void UseLayouts(LayoutSet layouts)
    {
        _layouts = layouts;
        _reader = new StashReader(layouts, _icons, _counts);
    }

    /// <summary>Read the tab in <paramref name="screen"/>, the game window as the hotkey copied it.</summary>
    public async Task ReadAsync(Frame screen)
    {
        if (_reader is null || _layouts is null) { _status("Not ready yet: the tab layouts haven't loaded"); return; }
        if (Interlocked.Exchange(ref _busy, 1) == 1) return; // the previous press is still being read
        try
        {
            var frame = screen.NormalisedTo1440();
            // The stash-open check looks at the same copy: its underline row sits at 1440p row
            // UnderlineRow, and this frame starts at row 0.
            if (!TabStrip.StashOpen(frame, top: 0)) { _status("The stash isn't open"); return; }

            var read = _reader.Read(frame);
            if (read is null) { _status("No special tab recognised on screen"); return; }
            var label = _layouts.Layouts[read.Tab.Layout].Label;
            var uncovered = TabStrip.StripUncovered(frame);
            var name = _namer.Choose(read.Tab.Layout, label, uncovered, uncovered ? await TabNameReader.ReadAsync(frame) : null);
            if (name is null) { _status("A tooltip covers the tab name: move the mouse off the tab buttons and press again"); return; }

            var snap = Snapshot.From(read, name, App.ToolVersion, _layouts.Patch, DateTimeOffset.UtcNow);
            _queue.Enqueue(snap);
            var summary = $"{label} \"{name}\": {snap.Reads.Length} stacks";
            _log($"read {summary} (score {read.Tab.Score:F2})");
            _status($"{DateTime.Now:HH:mm} {summary}");
            _queued();
        }
        catch (Exception e) { _log($"read failed: {e}"); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
}
