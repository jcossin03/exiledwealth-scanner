using System.Text.Json;
using EwCapture.Core;
using EwCapture.Core.Recognition;

namespace EwCapture;

/// <summary>Tray icon, hotkey, auto reading, diagnostic captures and the upload loop.</summary>
sealed class App : ApplicationContext
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExiledWealth");
    /// <summary>From the csproj's Version, so releases and the app never disagree.</summary>
    public static readonly string Version = typeof(App).Assembly.GetName().Version!.ToString(3);
    public static readonly string ToolVersion = $"ew-capture {Version}";
    const string HotkeyText = "Ctrl+Shift+F12";

    readonly NotifyIcon _tray;
    readonly HotkeyWindow _hotkey;
    static readonly string LogPath = Path.Combine(DataDir, "log.txt");
    readonly UploadQueue _queue = new(Path.Combine(DataDir, "queue"));
    readonly SnapshotQueue _snapshots = new(Path.Combine(DataDir, "snapshots"));
    static readonly string LayoutsPath = Path.Combine(DataDir, "layouts.json");
    readonly IconCache _icons;
    readonly AutoReader _auto;
    readonly System.Windows.Forms.Timer _layoutRefresh = new() { Interval = 3_600_000 };
    readonly ToolStripMenuItem _autoItem = new("Read tabs automatically") { CheckOnClick = true, Checked = true };
    readonly ToolStripMenuItem _readItem = new() { Enabled = false };
    readonly ToolStripMenuItem _versionItem = new() { Enabled = false };
    readonly ToolStripMenuItem _updateItem = new();
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 6 * 3_600_000 };
    readonly Updater _updater;
    string _updateStatus = "not checked yet";
    string _lastRead = "none yet";
    string? _layoutsVersion;
    SynchronizationContext? _ui;
    readonly ScannerApi _api;
    readonly System.Windows.Forms.Timer _retry = new() { Interval = 30_000 };
    // A new read waits this long for more: flipping through ten tabs is one request, not ten.
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 3_000 };
    readonly ToolStripMenuItem _accountItem = new() { Enabled = false };
    readonly ToolStripMenuItem _queueItem = new() { Enabled = false };
    readonly ToolStripMenuItem _lastItem = new() { Enabled = false };
    readonly ToolStripMenuItem _labelItem = new();
    readonly ToolStripMenuItem _linkItem = new();
    string? _account;
    string? _label;
    // Notifications are hidden while a game is in front, so the menu says how the last upload went.
    string _lastUpload = "none yet";
    bool _draining;

    /// <summary>Diagnostic screenshots are a dev-only tool: the live site never takes them.</summary>
    static readonly bool Diagnostics = new Uri(LoadBaseUrl()).Host == "dev.exiledwealth.com";

    public App()
    {
        var http = new HttpClient { BaseAddress = new Uri(LoadBaseUrl()), Timeout = TimeSpan.FromSeconds(60) };
        TokenStore.UseServer(http.BaseAddress);
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ew-capture/{Version} (+https://exiledwealth.com)");
        _api = new ScannerApi(http, TokenStore.Load());
        _api.Log = line => { Log(line); _lastUpload = $"{DateTime.Now:HH:mm} {line}"; };
        Log($"start {ToolVersion}, server {http.BaseAddress}, linked {_api.Token is not null}, tab-name OCR {(TabNameReader.Available ? "on" : "unavailable")}");
        // Screenshots queued for dev must never go to the live site (older versions could queue them there).
        if (!Diagnostics && Directory.Exists(_queue.Directory))
            try { Directory.Delete(_queue.Directory, recursive: true); Log("removed queued screenshots: not used outside dev"); }
            catch (IOException e) { Log($"couldn't remove queued screenshots: {e.Message}"); }
        _icons = new IconCache(Path.Combine(DataDir, "icons"), Log);
        _updater = new Updater(LoadBaseUrl(), Log);
        _updateItem.Click += async (_, _) =>
        {
            if (_updater.ReadyVersion is not null) { Log($"restarting to update to {_updater.ReadyVersion}"); _updater.RestartToApply(); return; }
            await CheckUpdates();
        };
        _auto = new AutoReader(_snapshots, _icons, Log,
            status => OnUi(() => { _lastRead = status; RefreshMenu(); }),
            () => OnUi(() => { _debounce.Stop(); _debounce.Start(); }));
        _autoItem.CheckedChanged += (_, _) => { _auto.Enabled = _autoItem.Checked; Log($"auto read {(_autoItem.Checked ? "on" : "off")}"); };

        _labelItem.Click += (_, _) => AskLabel();
        _linkItem.Click += async (_, _) => await LinkOrUnlink();
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _accountItem,
            _readItem,
            _queueItem,
            _lastItem,
            new ToolStripSeparator(),
            _autoItem,
            new ToolStripMenuItem($"Read now ({HotkeyText})", null, (_, _) => ReadNowFromMenu()),
            .. (Diagnostics ? new ToolStripItem[] { new ToolStripMenuItem("Send diagnostic screenshot", null, (_, _) => CaptureNow()) } : []),
            // Screenshot tools exist only on dev (see Diagnostics).
            .. (Diagnostics ? new ToolStripItem[] { _labelItem, new ToolStripMenuItem("Open queue folder", null, (_, _) => OpenQueueFolder()) } : []),
            new ToolStripMenuItem("Open log", null, (_, _) => OpenLog()),
            .. (Diagnostics ? new ToolStripItem[] { new ToolStripMenuItem("Retry rejected captures", null, async (_, _) => await RetryRejected()) } : []),
            new ToolStripSeparator(),
            _versionItem,
            _updateItem,
            new ToolStripSeparator(),
            _linkItem,
            new ToolStripMenuItem("Quit", null, (_, _) => Quit()),
        ]);

        _tray = new NotifyIcon
        {
            Icon = TrayIcon(),
            Text = "Exiled Wealth",
            ContextMenuStrip = menu,
            Visible = true,
        };

        // The menu is a control, so WinForms has installed its UI context by now.
        _ui = SynchronizationContext.Current;
        _hotkey = new HotkeyWindow(Native.MOD_CONTROL | Native.MOD_SHIFT, Keys.F12, ReadNow);
        Log($"hotkey {HotkeyText} registered: {_hotkey.Registered}");
        if (!_hotkey.Registered)
            Notify($"{HotkeyText} is taken by another program. Use the tray menu's Read now instead.", ToolTipIcon.Warning);

        _retry.Tick += async (_, _) => await Drain();
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await Drain(); };
        _retry.Start();
        _layoutRefresh.Tick += async (_, _) => await LoadLayouts();
        _layoutRefresh.Start();
        _updateTimer.Tick += async (_, _) => await CheckUpdates();
        _updateTimer.Start();
        _ = CheckUpdates();
        RefreshMenu();
        _ = Startup();
    }

    static string ConfiguredBaseUrl()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
            if (doc.RootElement.TryGetProperty("baseUrl", out var v) && v.GetString() is { } url)
                return url.EndsWith('/') ? url : url + "/";
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return "https://exiledwealth.com/";
    }

    /// <summary>The server to use: appsettings.json's, if it's https, else the live site.</summary>
    static string LoadBaseUrl()
    {
        var url = ConfiguredBaseUrl();
        // The device token and every update come from here: never over plain http.
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? url : "https://exiledwealth.com/";
    }

    async Task Startup()
    {
        if (_api.Token is null)
        {
            Notify("Not linked yet. Right-click the tray icon and choose Link to account.", ToolTipIcon.Info);
            return;
        }
        try
        {
            var who = await _api.WhoAmIAsync();
            if (who is null) { Unlinked(); return; }
            _account = who.AccountName;
            RefreshMenu();
            await LoadLayouts();
            await Drain();
        }
        catch (Exception e) when (e is HttpRequestException or ScannerException or TaskCanceledException)
        {
            // Offline at start-up is fine: reads queue until the server answers,
            // using the slot maps from last time.
            Log($"start-up check failed: {e.Message}");
            LoadCachedLayouts();
            RefreshMenu();
        }
    }

    async Task CheckUpdates()
    {
        _updateStatus = "checking…";
        RefreshMenu();
        _updateStatus = await _updater.CheckAsync();
        RefreshMenu();
    }

    /// <summary>Quit, installing a downloaded update on the way out.</summary>
    void Quit()
    {
        _updater.ApplyOnExit();
        ExitThread();
    }

    void OnUi(Action a) { if (_ui is null) a(); else _ui.Post(_ => a(), null); }

    /// <summary>The hotkey: read the open tab now, and on dev also send a screenshot for diagnosis.</summary>
    void ReadNow()
    {
        _ = _auto.ReadNowAsync();
        if (_api.Token is not null && Diagnostics) CaptureNow();
    }

    void ReadNowFromMenu()
    {
        Notify("Switch to the game: it reads as soon as Path of Exile 2 is in front (within 10 s).", ToolTipIcon.Info);
        _ = _auto.ReadWhenGameInFrontAsync(TimeSpan.FromSeconds(10));
    }

    void LoadCachedLayouts()
    {
        if (_layoutsVersion is not null || !File.Exists(LayoutsPath)) return;
        try { UseLayouts(LayoutSet.Parse(File.ReadAllText(LayoutsPath))); }
        catch (Exception e) when (e is IOException or JsonException) { Log($"cached layouts unreadable: {e.Message}"); }
    }

    /// <summary>The slot maps: the cached copy at once, then the server's if it has a newer one.</summary>
    async Task LoadLayouts()
    {
        LoadCachedLayouts();
        if (_api.Token is null) return;
        try
        {
            var fresh = await _api.GetLayoutsAsync(_layoutsVersion);
            if (fresh is null) return; // ours is current
            var set = LayoutSet.Parse(fresh.Value.Json);
            Directory.CreateDirectory(DataDir);
            await File.WriteAllTextAsync(LayoutsPath, fresh.Value.Json);
            UseLayouts(set);
        }
        catch (Exception e) when (e is HttpRequestException or ScannerException or TaskCanceledException or JsonException or IOException)
        {
            Log($"layouts refresh failed: {e.Message}");
        }
    }

    void UseLayouts(LayoutSet set)
    {
        _layoutsVersion = set.Version;
        _auto.UseLayouts(set);
        Log($"layouts {set.Version} (patch {set.Patch}): {set.Layouts.Count} tabs");
        var icons = set.Layouts.Values.SelectMany(l => l.Slots).Select(s => s.Icon).OfType<string>().ToList();
        _ = Task.Run(() => _icons.PrefetchAsync(icons));
    }

    void CaptureNow()
    {
        if (!Diagnostics) return;
        GameWindow.Capture? capture;
        string? problem;
        try { (capture, problem) = GameWindow.Grab(); }
        catch (Exception e) { Log($"capture crashed: {e}"); Notify($"Capture failed: {e.Message}", ToolTipIcon.Error); return; }
        if (capture is null) { Log($"capture skipped: {problem}"); Notify(problem!, ToolTipIcon.Warning); return; }

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var meta = new CaptureMeta(
            ToolVersion,
            DateTimeOffset.UtcNow,
            string.IsNullOrEmpty(_label) ? null : _label,
            capture.Client,
            capture.Monitor,
            new GameInfo(capture.Process, capture.WindowMode, GameConfig.ReadRelevant(GameConfig.FindConfigFile(docs))));
        var png = capture.ToPng();
        var id = _queue.Enqueue(png, meta.ToJson());
        Log($"captured {id}: {capture.Client.Width}x{capture.Client.Height} {capture.WindowMode}, {png.Length:N0} bytes, label {_label ?? "-"}");

        var warn = capture.WindowMode == "windowed" ? " (game is windowed: borderless is better)" : "";
        Notify($"Captured {capture.Client.Width}x{capture.Client.Height}{warn}", ToolTipIcon.None);
        RefreshMenu();
        _ = Drain();
    }

    async Task Drain()
    {
        if (_draining || _api.Token is null) return;
        _draining = true;
        try
        {
            // Reads and seen notes go in one request. A note is best effort: lost, it only means a session shows that tab as not opened.
            var notes = _auto.Seen.TakeNotes();
            int pending = _queue.Pending().Count, reads = _snapshots.Pending().Count;
            if (pending + reads + notes.Count == 0) return;
            if (reads + notes.Count > 0)
            {
                var (sentReads, outcome) = await _snapshots.SyncAsync(_api, notes);
                Log($"sync: {sentReads} of {reads} reads stored, {notes.Count} seen notes, {_snapshots.Pending().Count} still waiting ({outcome})");
            }
            if (pending > 0 && Diagnostics)
            {
                var sent = await _queue.DrainAsync(_api);
                Log($"drain: {sent} of {pending} uploaded, {_queue.Pending().Count} still waiting");
            }
            // A 401 leaves the queues intact; check whether that's what stopped them.
            if (_queue.Pending().Count + _snapshots.Pending().Count > 0 && await _api.WhoAmIAsync() is null) Unlinked();
        }
        // Anything escaping here would take down the tray app; log it and try again next tick.
        catch (Exception e) { Log($"drain failed: {e}"); }
        finally
        {
            _draining = false;
            RefreshMenu();
        }
    }

    async Task LinkOrUnlink()
    {
        if (_api.Token is not null)
        {
            if (MessageBox.Show("Unlink this PC from your Exiled Wealth account?", "Exiled Wealth",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            TokenStore.Clear();
            _api.Forget();
            _account = null;
            RefreshMenu();
            return;
        }

        var code = Prompt.Ask("Link to Exiled Wealth",
            "On the Exiled Wealth site, open the Scanner page and choose Get a link code. Type the code shown there:",
            upperCase: true);
        if (string.IsNullOrEmpty(code)) return;
        try
        {
            var result = await _api.LinkAsync(code, Environment.MachineName);
            TokenStore.Save(result.Token);
            _account = result.AccountName;
            Notify($"Linked to {result.AccountName}. Open your special tabs in game and they are read automatically.", ToolTipIcon.Info);
            RefreshMenu();
            await LoadLayouts();
            await Drain();
        }
        catch (Exception e) when (e is HttpRequestException or ScannerException or TaskCanceledException)
        {
            MessageBox.Show($"Couldn't link: {e.Message}", "Exiled Wealth", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void Unlinked()
    {
        TokenStore.Clear();
        _api.Forget();
        _account = null;
        RefreshMenu();
        Notify("This PC was unlinked from your account. Link again from the tray menu; queued captures are kept.", ToolTipIcon.Warning);
    }

    void AskLabel()
    {
        // Sticky until changed, so labelling never needs a dialog over the game.
        var label = Prompt.Ask("Capture label",
            "Describe what the next captures show, e.g. \"currency tab, big stacks\". Leave empty for none.",
            _label ?? "");
        if (label is null) return;
        _label = label.Length == 0 ? null : label[..Math.Min(label.Length, 200)];
        RefreshMenu();
    }

    void OpenQueueFolder()
    {
        Directory.CreateDirectory(_queue.Directory);
        System.Diagnostics.Process.Start("explorer.exe", _queue.Directory);
    }

    void RefreshMenu()
    {
        var linked = _api.Token is not null;
        _accountItem.Text = linked ? $"Linked: {_account ?? "(checking…)"}" : "Not linked";
        _readItem.Text = $"Last read: {_lastRead}";
        _versionItem.Text = $"Version {Version}: {_updateStatus}";
        _updateItem.Text = _updater.ReadyVersion is { } v ? $"Restart to update to {v}" : "Check for updates";
        _updateItem.Enabled = _updater.Installed;
        _queueItem.Text = Diagnostics
            ? $"Waiting to upload: {_snapshots.Pending().Count} reads, {_queue.Pending().Count} screenshots"
            : $"Waiting to upload: {_snapshots.Pending().Count} reads";
        _lastItem.Text = $"Last upload: {_lastUpload}";
        _labelItem.Text = _label is null ? "Set capture label…" : $"Label: {_label} (change…)";
        _linkItem.Text = linked ? "Unlink this PC…" : "Link to account…";
    }

    async Task RetryRejected()
    {
        var n = _queue.RequeueRejected();
        Log($"requeued {n} rejected captures");
        _lastUpload = $"{DateTime.Now:HH:mm} requeued {n} rejected";
        RefreshMenu();
        await Drain();
    }

    /// <summary>The embedded mark at the tray's own size, so Windows doesn't blur a 32px one down.</summary>
    static Icon TrayIcon()
    {
        using var s = typeof(App).Assembly.GetManifestResourceStream("ew.ico");
        return s is null ? SystemIcons.Application : new Icon(s, SystemInformation.SmallIconSize);
    }

    void OpenLog()
    {
        Directory.CreateDirectory(DataDir);
        if (!File.Exists(LogPath)) File.WriteAllText(LogPath, "");
        System.Diagnostics.Process.Start("notepad.exe", LogPath);
    }

    /// <summary>One timestamped line per event. Never throws: logging must not break capturing.</summary>
    static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            // Start over past 1 MB rather than grow forever.
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    void Notify(string text, ToolTipIcon icon) => _tray.ShowBalloonTip(3000, "Exiled Wealth", text, icon);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _retry.Dispose();
            _debounce.Dispose();
            _layoutRefresh.Dispose();
            _updateTimer.Dispose();
            _auto.Dispose();
            _hotkey.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
