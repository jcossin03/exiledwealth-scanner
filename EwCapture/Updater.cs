using Velopack;
using Velopack.Sources;

namespace EwCapture;

/// <summary>
/// Self-update through Velopack, from releases we host on our own site. Updates
/// download in the background and are applied when the user picks "Restart to
/// update" or when the app next exits: never in the middle of play.
/// </summary>
sealed class Updater(string baseUrl, Action<string> log)
{
    // Only feeds signed with our update key are believed (SignedFeedDownloader).
    readonly UpdateManager _mgr = new(new SimpleWebSource(baseUrl + "api/scanner/releases", new SignedFeedDownloader()));
    UpdateInfo? _ready;
    int _checking;

    /// <summary>False for a plain copied exe: Velopack can only update what it installed.</summary>
    public bool Installed => _mgr.IsInstalled;
    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    /// <summary>Check and download. Returns a line for the tray menu.</summary>
    public async Task<string> CheckAsync()
    {
        if (!Installed) return "updates need the installed app (see the Exiled Wealth site)";
        if (Interlocked.Exchange(ref _checking, 1) == 1) return "already checking";
        try
        {
            var info = await _mgr.CheckForUpdatesAsync();
            if (info is null) { log("updates: up to date"); return $"up to date ({DateTime.Now:HH:mm})"; }
            log($"updates: downloading {info.TargetFullRelease.Version}");
            await _mgr.DownloadUpdatesAsync(info);
            _ready = info;
            log($"updates: {info.TargetFullRelease.Version} ready");
            return $"{info.TargetFullRelease.Version} ready: restart to update";
        }
        catch (Exception e)
        {
            log($"updates: check failed: {e.Message}");
            return "update check failed (see log)";
        }
        finally { Interlocked.Exchange(ref _checking, 0); }
    }

    public void RestartToApply()
    {
        if (_ready is not null) _mgr.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
    }

    /// <summary>On quit: install a downloaded update once this process is gone, without restarting.</summary>
    public void ApplyOnExit()
    {
        if (_ready is not null) _mgr.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: false);
    }
}
