using EwCapture.Core;
using Velopack;
using Velopack.Sources;

namespace EwCapture;

/// <summary>
/// Self-update through Velopack, from releases we host on our own site. Updates
/// download in the background and are applied when the user picks "Restart to
/// update" or when the app next exits: never in the middle of play. Where the check
/// stands is one <see cref="UpdateState"/>, which both of the tray's update lines show.
/// </summary>
sealed class Updater
{
    readonly UpdateManager _mgr;
    readonly Action<string> _log;
    UpdateInfo? _ready; // what State.Ready names, for Velopack

    public Updater(ServerProfile server, Action<string> log)
    {
        // Only feeds signed with our update key are believed (SignedFeedDownloader).
        _mgr = new(new SimpleWebSource(server.BaseUrl + "api/scanner/releases", new SignedFeedDownloader()));
        _log = log;
        State = UpdateState.Start(_mgr.IsInstalled);
    }

    public UpdateState State { get; private set; }

    /// <summary>
    /// Check and download, unless a check is already running or this is a plain copied exe (then nothing
    /// changes). State is Checking as soon as this returns its task, so the caller can show it at once.
    /// </summary>
    public async Task CheckAsync()
    {
        if (!State.CanCheck) return;
        State = State.Checking();
        try
        {
            var info = await _mgr.CheckForUpdatesAsync();
            if (info is null) { _log("updates: up to date"); State = State.UpToDate(DateTime.Now); return; }
            _log($"updates: downloading {info.TargetFullRelease.Version}");
            await _mgr.DownloadUpdatesAsync(info);
            _ready = info;
            State = State.Downloaded(info.TargetFullRelease.Version.ToString(), DateTime.Now);
            _log($"updates: {info.TargetFullRelease.Version} ready");
        }
        catch (Exception e)
        {
            _log($"updates: check failed: {e.Message}");
            State = State.Failed();
        }
    }

    /// <summary>Install the downloaded update and start it. On success this process exits here; it returns only when there was nothing to install or Velopack failed.</summary>
    public void RestartToApply()
    {
        if (_ready is null) return;
        try { _mgr.ApplyUpdatesAndRestart(_ready.TargetFullRelease); }
        catch (Exception e) { _log($"updates: couldn't install {_ready.TargetFullRelease.Version}: {e.Message}"); }
    }

    /// <summary>On quit: install a downloaded update once this process is gone, without restarting.</summary>
    public void ApplyOnExit()
    {
        if (_ready is not null) _mgr.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: false);
    }
}
