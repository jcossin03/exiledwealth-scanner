using System.Security.Cryptography;
using System.Text.Json;
using EwCapture.Core;
using EwCapture.Core.Recognition;

namespace EwCapture;

/// <summary>Tray icon, the read key, diagnostic captures and the upload loop.</summary>
sealed class App : ApplicationContext
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExiledWealth");
    /// <summary>From the csproj's Version, so releases and the app never disagree.</summary>
    public static readonly string Version = typeof(App).Assembly.GetName().Version!.ToString(3);
    public static readonly string ToolVersion = $"ew-capture {Version}";
    /// <summary>The read key the player picked; absent, <see cref="ReadKey.Default"/>.</summary>
    static readonly string ReadKeyPath = Path.Combine(DataDir, "read-key.txt");
    /// <summary>Present once the "reads need a key press now" notice has been shown.</summary>
    static readonly string KeypressNoticePath = Path.Combine(DataDir, "keypress-notice.txt");

    readonly NotifyIcon _tray;
    // The only trigger for a read: GGG allows a screen read only once per manual key press.
    HotkeyWindow _hotkey;
    ReadKey _readKey;
    static readonly string LogPath = Path.Combine(DataDir, "log.txt");
    // Queues are per server: a read taken while pointed at dev must never reach the live site.
    readonly UploadQueue _queue = new(Path.Combine(ServerDataDir, "queue"));
    readonly SnapshotQueue _snapshots = new(Path.Combine(ServerDataDir, "snapshots"));
    static readonly string LayoutsPath = Path.Combine(DataDir, "layouts.json");
    readonly IconCache _icons;
    readonly TabReader _tabs;
    readonly System.Windows.Forms.Timer _layoutRefresh = new() { Interval = 3_600_000 };
    readonly ToolStripMenuItem _readKeyItem = new();
    readonly ToolStripMenuItem _readItem = new() { Enabled = false };
    readonly ToolStripMenuItem _versionItem = new() { Enabled = false };
    readonly ToolStripMenuItem _updateItem = new();
    // Opt-in: install a downloaded update by restarting on its own, but only while the game isn't running.
    // Off by default; nobody gets restarted without having asked for it. Remembered as a marker file.
    static readonly string AutoUpdatePath = Path.Combine(DataDir, "auto-update.txt");
    readonly ToolStripMenuItem _autoUpdateItem = new("Install updates automatically (when the game is closed)") { CheckOnClick = true, Checked = File.Exists(AutoUpdatePath) };
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 6 * 3_600_000 };
    readonly Updater _updater;
    string _updateStatus = "not checked yet";
    string _lastRead = "none yet";
    string? _layoutsVersion;
    SynchronizationContext? _ui;
    readonly ScannerApi _api;
    // Retries start at 30 s. While the server can't take uploads they stretch toward five minutes, or to
    // what Retry-After asks: an outage must not be met by every scanner knocking twice a minute.
    static readonly TimeSpan RetryMin = TimeSpan.FromSeconds(30), RetryMax = TimeSpan.FromMinutes(5);
    readonly System.Windows.Forms.Timer _retry = new() { Interval = (int)RetryMin.TotalMilliseconds };
    DateTime _nextAttempt = DateTime.MinValue;
    // Reads are sent in batches (UploadBatch decides): a new read waits 30 s for the next press, so reading
    // ten tabs in a row is one request, not ten, and a long session still goes up every two minutes. The
    // debounce timer wakes us when the presses stop; the retry tick leaves a collecting batch alone.
    readonly UploadBatch _batch = new();
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = (int)UploadBatch.Quiet.TotalMilliseconds };
    // A start-up that couldn't reach the server leaves the account unknown and the slot maps as cached:
    // the first upload that gets through asks again (no polling). True while that catch-up is out.
    bool _catchingUp;
    // The last "update ready; waiting for …" line logged, so the retry tick doesn't repeat it.
    string? _updateWaitLogged;
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
    // From "Link to account…" until the link answers: a second click mustn't start a second link.
    bool _linking;

    // Static initializers run in textual order: everything LoadBaseUrl() reads must be declared above its
    // first caller (Diagnostics, below). 0.4.7 to 0.4.10 had these after it and died on launch.
    /// <summary>The servers an install may point at: the live site and dev, nothing else, so no file can redirect uploads elsewhere.</summary>
    static readonly string[] Servers = ["https://exiledwealth.com/", "https://dev.exiledwealth.com/"];
    /// <summary>The server chosen from the tray menu; absent, appsettings.json decides.</summary>
    static readonly string ServerPath = Path.Combine(DataDir, "server.txt");
    /// <summary>Diagnostic screenshots are a dev-only tool: the live site never takes them.</summary>
    static readonly bool Diagnostics = new Uri(LoadBaseUrl()).Host == "dev.exiledwealth.com";
    /// <summary>Where this server's queues live. The live site keeps the original folders, so existing installs lose nothing.</summary>
    static readonly string ServerDataDir = new Uri(LoadBaseUrl()).Host == "exiledwealth.com" ? DataDir : Path.Combine(DataDir, new Uri(LoadBaseUrl()).Host);
    /// <summary>Set by a server switch: Program starts a fresh copy once this one has let go of its mutex.</summary>
    public static bool RestartRequested { get; private set; }

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
        // Reads an older version left waiting were taken on its 2 s timer: the site refuses them, so don't send them.
        var stale = _snapshots.SetAsideOlderThan(SnapshotQueue.MinQueuedReadVersion);
        if (stale > 0) Log($"set aside {stale} reads taken by a version before {SnapshotQueue.MinQueuedReadVersion}");
        _icons = new IconCache(Path.Combine(DataDir, "icons"), Log);
        _updater = new Updater(LoadBaseUrl(), Log);
        _updateItem.Click += async (_, _) =>
        {
            if (_updater.ReadyVersion is not null) { Log($"restarting to update to {_updater.ReadyVersion}"); _updater.RestartToApply(); return; }
            await CheckUpdates();
        };
        _tabs = new TabReader(_snapshots, _icons, Log,
            status => OnUi(() => { _lastRead = status; RefreshMenu(); }),
            () => OnUi(Queued));
        _readKeyItem.Click += (_, _) => ChangeReadKey();
        _autoUpdateItem.CheckedChanged += (_, _) =>
        {
            try { if (_autoUpdateItem.Checked) File.WriteAllText(AutoUpdatePath, "on"); else File.Delete(AutoUpdatePath); }
            catch (IOException e) { Log($"couldn't remember the auto-update setting: {e.Message}"); }
            Log($"auto update {(_autoUpdateItem.Checked ? "on" : "off")}");
            if (_autoUpdateItem.Checked) ApplyUpdateIfIdle();
        };

        _labelItem.Click += (_, _) => AskLabel();
        _linkItem.Click += async (_, _) => await LinkOrUnlink();
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _accountItem,
            _readItem,
            _queueItem,
            _lastItem,
            new ToolStripSeparator(),
            _readKeyItem,
            // Screenshot tools exist only on dev (see Diagnostics). The read key takes the screenshots there.
            .. (Diagnostics ? new ToolStripItem[] { _labelItem, new ToolStripMenuItem("Open queue folder", null, (_, _) => OpenQueueFolder()) } : []),
            new ToolStripMenuItem("Open log", null, (_, _) => OpenLog()),
            .. (Diagnostics ? new ToolStripItem[] { new ToolStripMenuItem("Retry rejected captures", null, async (_, _) => await RetryRejected()) } : []),
            new ToolStripSeparator(),
            ServerMenu(),
            _versionItem,
            _updateItem,
            _autoUpdateItem,
            new ToolStripSeparator(),
            _linkItem,
            new ToolStripMenuItem("Quit", null, (_, _) => Quit()),
            new ToolStripSeparator(),
            // GGG's third-party notice, which their developer docs ask every public tool to show.
            new ToolStripMenuItem("Not affiliated with or endorsed by Grinding Gear Games") { Enabled = false },
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
        _readKey = LoadReadKey();
        _hotkey = Register(_readKey);
        Log($"read key {_readKey} registered: {_hotkey.Registered}");
        if (!_hotkey.Registered)
            Notify($"{_readKey} is taken by another program. Pick another read key from the tray menu.", ToolTipIcon.Warning);
        else if (!File.Exists(KeypressNoticePath))
        {
            // GGG's overlay rules: read the screen only "as long as the user is aware of what you are doing with that data".
            Notify($"Press {_readKey} to read a stash tab. Its item counts go to your Exiled Wealth account.", ToolTipIcon.Info);
            try { Directory.CreateDirectory(DataDir); File.WriteAllText(KeypressNoticePath, "shown"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        _retry.Tick += async (_, _) =>
        {
            // Retries only: a batch still collecting goes up when the presses stop (the debounce), or
            // at UploadBatch.Max. Backoff and Retry-After hold this through _nextAttempt.
            if (_batch.DrainNow(DateTime.UtcNow, _nextAttempt)) await Drain();
            // Opted in, an update waits for the game to close: this tick notices when it has. Not while
            // the Link prompt is open: a restart would close it under the player.
            if (_autoUpdateItem.Checked && !_linking) ApplyUpdateIfIdle();
        };
        _debounce.Tick += async (_, _) => await Flush();
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

    // No initializer, so declaration order doesn't matter: false until the first warning.
    static bool _warnedConfig;

    /// <summary>
    /// The server to use: the one chosen from the tray menu, else appsettings.json's, else the live site.
    /// Both must be one of <see cref="Servers"/>: the device token and every update come from here.
    /// </summary>
    static string LoadBaseUrl()
    {
        try { if (File.ReadAllText(ServerPath).Trim() is { } chosen && Servers.Contains(chosen)) return chosen; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NullReferenceException) { }
        var url = ConfiguredBaseUrl();
        if (Servers.Contains(url)) return url;
        if (!_warnedConfig) { _warnedConfig = true; Log($"appsettings.json names {url}, which isn't one of ours: using {Servers[0]}"); }
        return Servers[0];
    }

    async Task Startup()
    {
        if (_api.Token is not { } token)
        {
            Notify("Not linked yet. Right-click the tray icon and choose Link to account.", ToolTipIcon.Info);
            return;
        }
        try
        {
            var who = await _api.WhoAmIAsync();
            if (who is null) { Unlinked(token); return; }
            _account = who.AccountName;
            RefreshMenu();
            await LoadLayouts();
            await Drain();
        }
        catch (Exception e) when (e is HttpRequestException or ScannerException or TaskCanceledException)
        {
            // Offline at start-up is fine: reads queue until the server answers, using the slot maps
            // from last time. The first upload that gets through finishes this check (CatchUpAccount).
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
        if (_autoUpdateItem.Checked) ApplyUpdateIfIdle();
    }

    /// <summary>
    /// With the opt-in on and an update downloaded: restart into it now, unless the game is running or an
    /// upload is mid-flight. Queued reads are files on disk and survive the restart. Called after each
    /// update check and on every retry tick, so a wait ends soon after the game closes; each wait is logged once.
    /// </summary>
    void ApplyUpdateIfIdle()
    {
        if (_updater.ReadyVersion is not { } v) return;
        var waitingFor = GameWindow.GameRunning() ? "the game to close" : _draining ? "the upload to finish" : null;
        if (waitingFor is not null)
        {
            var line = $"update {v} ready; waiting for {waitingFor}";
            if (line != _updateWaitLogged) Log(line);
            _updateWaitLogged = line;
            return;
        }
        Log($"installing {v} automatically (opted in, game closed)");
        _updater.RestartToApply();
    }

    /// <summary>Quit, installing a downloaded update on the way out.</summary>
    void Quit()
    {
        _updater.ApplyOnExit();
        ExitThread();
    }

    /// <summary>Server: the live site or dev. Each has its own token and queues; switching restarts the app.</summary>
    ToolStripMenuItem ServerMenu()
    {
        var current = LoadBaseUrl();
        var menu = new ToolStripMenuItem($"Server: {new Uri(current).Host}");
        foreach (var url in Servers)
        {
            var item = new ToolStripMenuItem(new Uri(url).Host) { Checked = url == current };
            item.Click += (_, _) => SwitchServer(url);
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    void SwitchServer(string url)
    {
        if (!Servers.Contains(url) || url == LoadBaseUrl()) return;
        // Dev turns on diagnostics (screenshot uploads): nobody ends up there by curiosity without being told.
        if (new Uri(url).Host == "dev.exiledwealth.com" && MessageBox.Show(
                "dev.exiledwealth.com is the developer's test server, not the live site. It has its own login, and your stash there starts empty.\n\n"
                + $"While this PC is linked to it, the read key ({_readKey}) also uploads a screenshot of the game window, "
                + "with the display and UI sections of the game's settings file, to help fix misreads.\n\n"
                + "Switch to the test server?",
                "Exiled Wealth", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(ServerPath, url);
        Log($"server switched to {url}: restarting");
        RestartRequested = true;
        Quit();
    }

    void OnUi(Action a) { if (_ui is null) a(); else _ui.Post(_ => a(), null); }

    /// <summary>
    /// The read key: one screen copy of the game, read for the open tab. On dev the same copy is also the
    /// diagnostic screenshot, so a press never makes two copies (GGG: once per manual key press).
    /// </summary>
    void ReadNow()
    {
        // The last press is still in its tab-name OCR: say so, and don't copy the screen for nothing.
        if (_tabs.Busy) { _lastRead = "Still reading the last press: press again"; RefreshMenu(); return; }
        var diagnose = Diagnostics && _api.Token is not null;
        GameWindow.Capture capture;
        try
        {
            // Only ever the game in front, so another window is never read.
            var (window, problem) = GameWindow.Find(onlyIfInFront: true);
            if (window is null) { _lastRead = problem!; RefreshMenu(); return; }
            capture = GameWindow.Grab(window, panelOnly: !diagnose);
            // A copy holds whatever covered the game: keep it only if the game was still in front.
            if (Native.GetForegroundWindow() != window.Handle) { _lastRead = "The game left the front during the read"; RefreshMenu(); return; }
        }
        catch (Exception e) { Log($"read failed: {e}"); return; }
        _ = _tabs.ReadAsync(capture.Frame);
        if (diagnose) QueueDiagnostic(capture);
    }

    /// <summary>The saved read key, or the default when there's none (or it's unreadable).</summary>
    static ReadKey LoadReadKey()
    {
        try
        {
            if (File.Exists(ReadKeyPath) && ReadKey.Parse(File.ReadAllText(ReadKeyPath)) is { } k && k.Blocker() is null) return k;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log($"read key unreadable: {e.Message}"); }
        return ReadKey.Default;
    }

    HotkeyWindow Register(ReadKey key) => new(
        (key.Ctrl ? Native.MOD_CONTROL : 0) | (key.Alt ? Native.MOD_ALT : 0) | (key.Shift ? Native.MOD_SHIFT : 0),
        (Keys)key.Vk, ReadNow);

    /// <summary>Tray menu: pick a new read key by pressing it.</summary>
    void ChangeReadKey()
    {
        // Let go of the current key while the dialog is open, or pressing it there would read instead of record.
        _hotkey.Dispose();
        HotkeyWindow? chosen = null;
        bool TryUse(ReadKey key)
        {
            var window = Register(key);
            if (!window.Registered) { window.Dispose(); return false; }
            chosen?.Dispose();
            chosen = window;
            return true;
        }
        using (var dialog = new ReadKeyDialog(_readKey, TryUse))
        {
            dialog.ShowDialog();
            if (dialog.DialogResult == DialogResult.OK && dialog.Chosen is { } key && chosen is not null)
            {
                _hotkey = chosen;
                _readKey = key;
                try { Directory.CreateDirectory(DataDir); File.WriteAllText(ReadKeyPath, key.Store()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log($"couldn't save the read key: {e.Message}"); }
                Log($"read key changed to {key}");
                RefreshMenu();
                return;
            }
        }
        chosen?.Dispose();
        _hotkey = Register(_readKey);
        if (!_hotkey.Registered) Log($"read key {_readKey} couldn't be registered again");
        RefreshMenu();
    }

    /// <summary>A read was queued: wait for the presses to stop, unless this batch has waited long enough already.</summary>
    void Queued()
    {
        var now = DateTime.UtcNow;
        _batch.Add(now);
        if (_batch.DrainNow(now, _nextAttempt)) { _ = Flush(); return; } // waited UploadBatch.Max already
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>The presses stopped (or the batch is old enough): send it. Drain takes the batch when it sends.</summary>
    async Task Flush()
    {
        _debounce.Stop();
        await Drain();
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
        _tabs.UseLayouts(set);
        Log($"layouts {set.Version} (patch {set.Patch}): {set.Layouts.Count} tabs");
        var icons = set.Layouts.Values.SelectMany(l => l.Slots).Select(s => s.Icon).OfType<string>().ToList();
        _ = Task.Run(() => _icons.PrefetchAsync(icons));
    }

    /// <summary>Dev only: queue the read key's screen copy as a diagnostic screenshot.</summary>
    void QueueDiagnostic(GameWindow.Capture capture)
    {
        if (!Diagnostics) return;

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
        // The token this drain sends with: a 401 unlinks only if it's still the current one (Unlinked).
        if (_draining || _api.Token is not { } token || DateTime.UtcNow < _nextAttempt) return;
        _draining = true;
        var synced = false;
        try
        {
            // This drain takes everything waiting, so the batch ends here; a press during the request starts a new one.
            _batch.Taken();
            _debounce.Stop();
            // Reads and seen notes go in one request. A note is best effort: lost, it only means a session shows that tab as not opened.
            var notes = _tabs.Seen.TakeNotes();
            int pending = _queue.Pending().Count, reads = _snapshots.Pending().Count;
            if (pending + reads + notes.Count == 0) return;
            var outcome = UploadOutcome.Stored;
            TimeSpan? retryAfter = null;
            if (reads + notes.Count > 0)
            {
                int sentReads;
                (sentReads, outcome, retryAfter) = await _snapshots.SyncAsync(_api, notes);
                Log($"sync: {sentReads} of {reads} reads stored, {notes.Count} seen notes, {_snapshots.Pending().Count} still waiting ({outcome})");
                synced = outcome == UploadOutcome.Stored;
            }
            if (pending > 0 && Diagnostics)
            {
                var sent = await _queue.DrainAsync(_api);
                Log($"drain: {sent} of {pending} uploaded, {_queue.Pending().Count} still waiting");
            }
            switch (outcome)
            {
                case UploadOutcome.Unlinked: Unlinked(token); break; // the 401 said so; no second request to ask
                case UploadOutcome.RetryLater: Backoff(retryAfter); break;
                default: _retry.Interval = (int)RetryMin.TotalMilliseconds; _nextAttempt = DateTime.MinValue; break;
            }
        }
        // Anything escaping here would take down the tray app; log it and try again next tick.
        catch (Exception e) { Log($"drain failed: {e}"); }
        finally
        {
            _draining = false;
            RefreshMenu();
        }
        if (synced && _account is null) await CatchUpAccount();
    }

    /// <summary>
    /// Start-up couldn't reach the server, so the menu still says "(checking…)" and the slot maps are last
    /// time's. The first upload that gets through ends that with one whoami and one layouts fetch. No
    /// polling: this only follows an upload that happened anyway, and if it fails the next one tries again.
    /// </summary>
    async Task CatchUpAccount()
    {
        if (_catchingUp || _account is not null || _api.Token is not { } token) return;
        _catchingUp = true;
        try
        {
            var who = await _api.WhoAmIAsync();
            if (who is null) { Unlinked(token); return; }
            if (_api.Token != token) return; // unlinked or linked again meanwhile: that set the account
            _account = who.AccountName;
            Log("account checked after a start-up that couldn't reach the server");
            RefreshMenu();
            await LoadLayouts();
        }
        // Anything escaping here would take down the tray app; the next upload that gets through tries again.
        catch (Exception e) { Log($"account check failed: {e.Message}"); }
        finally { _catchingUp = false; }
    }

    async Task LinkOrUnlink()
    {
        if (_linking) return; // the menu item is disabled meanwhile; this covers a click already on its way
        if (_api.Token is not null)
        {
            if (MessageBox.Show("Unlink this PC from your Exiled Wealth account?", "Exiled Wealth",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            ForgetToken();
            return;
        }

        // Linking: from the code prompt until the server answers, one link at a time.
        _linking = true;
        RefreshMenu();
        try
        {
            var code = Prompt.Ask("Link to Exiled Wealth",
                "On the Exiled Wealth site, open the Scanner page and choose Get a link code. Type the code shown there:",
                upperCase: true);
            if (string.IsNullOrEmpty(code)) return;
            LinkResult result;
            try { result = await _api.LinkAsync(code, Environment.MachineName); }
            catch (Exception e) when (e is HttpRequestException or ScannerException or TaskCanceledException)
            {
                MessageBox.Show($"Couldn't link: {e.Message}", "Exiled Wealth", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _account = result.AccountName;
            try
            {
                TokenStore.Save(result.Token);
                Notify($"Linked to {result.AccountName}. Open a special tab in game and press {_readKey}: its item counts go to your account.", ToolTipIcon.Info);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
            {
                // Linked for this session (the token is in memory), but it won't survive a restart: say so.
                Log($"couldn't save the device token: {e.Message}");
                MessageBox.Show($"Linked to {result.AccountName}, but this PC couldn't save the link ({e.Message}). Reads upload until Exiled Wealth closes; after that, link again.",
                    "Exiled Wealth", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _linking = false;
            RefreshMenu();
        }
        await LoadLayouts();
        await Drain();
    }

    /// <summary>
    /// The next try waits for Retry-After when the server gave one, else twice as long as last time, up to
    /// five minutes. A wait past five minutes (the day's budget is spent) keeps the timer at five minutes and
    /// lets <see cref="Drain"/> return early until it has passed.
    /// </summary>
    void Backoff(TimeSpan? retryAfter)
    {
        var wait = retryAfter ?? TimeSpan.FromMilliseconds(Math.Min(_retry.Interval * 2, RetryMax.TotalMilliseconds));
        if (wait < RetryMin) wait = RetryMin;
        _retry.Interval = (int)Math.Min(wait.TotalMilliseconds, RetryMax.TotalMilliseconds);
        _nextAttempt = DateTime.UtcNow + wait;
        Log($"server can't take uploads right now: next try in {(int)wait.TotalSeconds} s");
    }

    /// <summary>
    /// The server refused <paramref name="token"/> (a 401). Unlink only if that's still this PC's token: an
    /// answer to a request sent before an unlink and a fresh link must not throw the new link away.
    /// </summary>
    void Unlinked(string token)
    {
        if (_api.Token != token) { Log("401 for a token this PC no longer uses: ignored"); return; }
        ForgetToken();
        Notify("This PC was unlinked from your account. Link again from the tray menu; queued captures are kept.", ToolTipIcon.Warning);
    }

    /// <summary>Drop the device token: from memory first, so a file that won't delete can't leave this session linked.</summary>
    void ForgetToken()
    {
        _api.Forget();
        _account = null;
        try { TokenStore.Clear(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log($"couldn't delete the device token file: {e.Message}"); }
        RefreshMenu();
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
        _readKeyItem.Text = _hotkey.Registered ? $"Read key: {_readKey} (change…)" : $"Read key: {_readKey} is taken by another program (change…)";
        _versionItem.Text = $"Version {Version}: {_updateStatus}";
        _updateItem.Text = _updater.ReadyVersion is { } v ? $"Restart to update to {v}" : "Check for updates";
        _updateItem.Enabled = _updater.Installed;
        _queueItem.Text = Diagnostics
            ? $"Waiting to upload: {_snapshots.Pending().Count} reads, {_queue.Pending().Count} screenshots"
            : $"Waiting to upload: {_snapshots.Pending().Count} reads";
        _lastItem.Text = $"Last upload: {_lastUpload}";
        _labelItem.Text = _label is null ? "Set capture label…" : $"Label: {_label} (change…)";
        _linkItem.Text = _linking ? "Linking…" : linked ? "Unlink this PC…" : "Link to account…";
        _linkItem.Enabled = !_linking;
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
            _hotkey.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
