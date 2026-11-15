using System.Security.Cryptography;
using System.Text.Json;
using EwCapture.Core;
using EwCapture.Core.Recognition;
// WinForms has a LinkState of its own (for LinkLabel); ours is the device link.
using LinkState = EwCapture.Core.LinkState;

namespace EwCapture;

/// <summary>Tray icon, the read key, diagnostic captures and the upload loop.</summary>
sealed class App : ApplicationContext
{
    // Static initializers run in textual order, so one that reads a static declared below it sees null.
    // 0.4.7 to 0.4.10 died on launch that way: Diagnostics called LoadBaseUrl() before Servers and
    // ServerPath were set. So no static initializer here reads another static of this class: DataDir and
    // Version read nothing of ours, every path below is computed when read (=>), and the server is worked
    // out once, in the constructor (ServerProfile.Resolve), never in a static.
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExiledWealth");
    /// <summary>From the csproj's Version, so releases and the app never disagree.</summary>
    public static readonly string Version = typeof(App).Assembly.GetName().Version!.ToString(3);
    public static string ToolVersion => $"ew-capture {Version}";
    /// <summary>The read key the player picked; absent, <see cref="ReadKey.Default"/>.</summary>
    static string ReadKeyPath => Path.Combine(DataDir, "read-key.txt");
    /// <summary>Present once the "reads need a key press now" notice has been shown.</summary>
    static string KeypressNoticePath => Path.Combine(DataDir, "keypress-notice.txt");
    static string LogPath => Path.Combine(DataDir, "log.txt");
    static string LayoutsPath => Path.Combine(DataDir, "layouts.json");
    // Opt-in: install a downloaded update by restarting on its own, but only while the game isn't running.
    // Off by default; nobody gets restarted without having asked for it. Remembered as a marker file.
    static string AutoUpdatePath => Path.Combine(DataDir, "auto-update.txt");
    // Retries start at 30 s. While the server can't take uploads they stretch toward five minutes, or to
    // what Retry-After asks: an outage must not be met by every scanner knocking twice a minute.
    static readonly TimeSpan RetryMin = TimeSpan.FromSeconds(30), RetryMax = TimeSpan.FromMinutes(5);
    /// <summary>Set by a server switch: Program starts a fresh copy once this one has let go of its mutex.</summary>
    public static bool RestartRequested { get; private set; }

    // The live site or dev, and the token file and queue folders that go with it. Worked out once (see above).
    readonly ServerProfile _server;
    readonly TokenStore _tokens;
    readonly NotifyIcon _tray;
    // The only trigger for a read: GGG allows a screen read only once per manual key press.
    HotkeyWindow _hotkey;
    ReadKey _readKey;
    // Queues are per server: a read taken while pointed at dev must never reach the live site.
    readonly UploadQueue _queue;
    readonly SnapshotQueue _snapshots;
    readonly IconCache _icons;
    readonly TabReader _tabs;
    readonly System.Windows.Forms.Timer _layoutRefresh = new() { Interval = 3_600_000 };
    readonly ToolStripMenuItem _readKeyItem = new();
    readonly ToolStripMenuItem _readItem = new() { Enabled = false };
    readonly ToolStripMenuItem _versionItem = new() { Enabled = false };
    readonly ToolStripMenuItem _updateItem = new();
    readonly ToolStripMenuItem _autoUpdateItem = new("Install updates automatically (when the game is closed)") { CheckOnClick = true, Checked = File.Exists(AutoUpdatePath) };
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 6 * 3_600_000 };
    // Where the update check stands (UpdateState in Core): both update lines in the menu come from it.
    readonly Updater _updater;
    string _lastRead = "none yet";
    string? _layoutsVersion;
    SynchronizationContext? _ui;
    readonly ScannerApi _api;
    readonly System.Windows.Forms.Timer _retry = new() { Interval = (int)RetryMin.TotalMilliseconds };
    DateTime _nextAttempt = DateTime.MinValue;
    // Reads are sent in batches (UploadBatch decides): a new read waits 30 s for the next press, so reading
    // ten tabs in a row is one request, not ten, and a long session still goes up every two minutes. The
    // debounce timer wakes us when the presses stop; the retry tick leaves a collecting batch alone.
    readonly UploadBatch _batch = new();
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = (int)UploadBatch.Quiet.TotalMilliseconds };
    // The last "update ready; waiting for …" line logged, so the retry tick doesn't repeat it.
    string? _updateWaitLogged;
    readonly ToolStripMenuItem _accountItem = new() { Enabled = false };
    readonly ToolStripMenuItem _queueItem = new() { Enabled = false };
    readonly ToolStripMenuItem _setAsideItem = new() { Enabled = false, Visible = false };
    readonly ToolStripMenuItem _lastItem = new() { Enabled = false };
    readonly ToolStripMenuItem _labelItem = new();
    readonly ToolStripMenuItem _linkItem = new();
    // Linked or not, and to whom (LinkState in Core). Moved only by Link(), which also does what a move means.
    LinkState _link;
    string? _label;
    // Notifications are hidden while a game is in front, so the menu says how the last upload went.
    string _lastUpload = "none yet";
    bool _draining;
    // Modal windows opened from the tray: the Read key window, the link and label prompts, message boxes.
    // The tray menu still opens behind them (a modal dialog doesn't disable the notify icon), so a menu item
    // that opens a window does nothing while one is up, and auto-update doesn't restart under one. A count,
    // since a message box can follow a prompt.
    int _dialogs;

    public App()
    {
        _server = ServerProfile.Resolve(DataDir, ReadText(Path.Combine(DataDir, ServerProfile.ChoiceFileName)),
            ReadText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")), out var serverWarning);
        if (serverWarning is not null) Log(serverWarning);
        _queue = new UploadQueue(_server.CapturesDir);
        _snapshots = new SnapshotQueue(_server.SnapshotsDir);
        _tokens = new TokenStore(_server);
        var http = new HttpClient { BaseAddress = _server.BaseUrl, Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ew-capture/{Version} (+https://exiledwealth.com)");
        var token = _tokens.Load();
        _api = new ScannerApi(http, token);
        _link = LinkState.FromToken(token);
        _api.Log = line => { Log(line); _lastUpload = $"{DateTime.Now:HH:mm} {line}"; };
        Log($"start {ToolVersion}, server {http.BaseAddress}, linked {token is not null}, tab-name OCR {(TabNameReader.Available ? "on" : "unavailable")}");
        // Screenshots queued for dev must never go to the live site (older versions could queue them there).
        if (!_server.IsDev && Directory.Exists(_queue.Directory))
            try { Directory.Delete(_queue.Directory, recursive: true); Log("removed queued screenshots: not used outside dev"); }
            catch (IOException e) { Log($"couldn't remove queued screenshots: {e.Message}"); }
        // Reads an older version left waiting were taken on its 2 s timer: the site refuses them, so don't send them.
        var stale = _snapshots.SetAsideOlderThan(SnapshotQueue.MinQueuedReadVersion);
        if (stale > 0) Log($"set aside {stale} reads taken by a version before {SnapshotQueue.MinQueuedReadVersion}");
        // Set-aside reads are kept to look at (or for their one more try) for a while, not forever.
        try
        {
            var old = _snapshots.DeleteSetAsideOlderThan(SnapshotQueue.KeepSetAside, DateTime.UtcNow);
            if (old > 0) Log($"deleted {old} set-aside reads older than {SnapshotQueue.KeepSetAside.TotalDays:0} days");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log($"couldn't delete old set-aside reads: {e.Message}"); }
        _icons = new IconCache(Path.Combine(DataDir, "icons"), Log);
        _updater = new Updater(_server, Log);
        _updateItem.Click += async (_, _) =>
        {
            if (_updater.State.Ready is { } v) { Log($"restarting to update to {v}"); _updater.RestartToApply(); return; }
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
            _setAsideItem,
            _lastItem,
            new ToolStripSeparator(),
            _readKeyItem,
            // Screenshot tools exist only on dev (ServerProfile.IsDev). The read key takes the screenshots there.
            .. (_server.IsDev ? new ToolStripItem[] { _labelItem, new ToolStripMenuItem("Open queue folder", null, (_, _) => OpenQueueFolder()) } : []),
            new ToolStripMenuItem("Open log", null, (_, _) => OpenLog()),
            .. (_server.IsDev ? new ToolStripItem[] { new ToolStripMenuItem("Retry rejected captures", null, async (_, _) => await RetryRejected()) } : []),
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
            // Opted in, an update waits for the game to close: this tick notices when it has.
            if (_autoUpdateItem.Checked) ApplyUpdateIfIdle();
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

    /// <summary>A small settings file's text, or null when it's missing or unreadable.</summary>
    static string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    async Task Startup()
    {
        if (_link.Kind == LinkKind.Unlinked)
            Notify("Not linked yet. Right-click the tray icon and choose Link to account.", ToolTipIcon.Info);
        else if (await CheckAccount())
        {
            await LoadLayouts();
            await Drain();
            return;
        }
        // Unlinked, refused, or the server couldn't say: last time's slot maps. Offline, reads queue until the
        // server answers, and the first upload that gets through finishes the check (Drain). Unlinked, a press
        // says to link first.
        LoadCachedLayouts();
        RefreshMenu();
    }

    /// <summary>
    /// Asks whose account the token is: at start-up, and after the first stored upload of a start-up that
    /// couldn't reach the server. No polling: an answer that doesn't come is asked again after the next
    /// stored upload. True once the account is confirmed.
    /// </summary>
    async Task<bool> CheckAccount()
    {
        if (!_link.NeedsAccountCheck || _link.Token is not { } token) return false;
        Link(new LinkEvent.AskingWho());
        try
        {
            var who = await _api.WhoAmIAsync();
            Link(who is null ? new LinkEvent.Refused(token)
                : who.AccountName is { Length: > 0 } account ? new LinkEvent.WhoAmI(token, account)
                : new LinkEvent.WhoAmIFailed(token));
        }
        // Anything escaping here would take down the tray app.
        catch (Exception e)
        {
            Log($"account check failed: {e.Message}");
            Link(new LinkEvent.WhoAmIFailed(token));
        }
        return _link.Kind == LinkKind.Linked && _link.IsCurrent(token);
    }

    /// <summary>
    /// Moves the link state, and does what the move means. When the token goes, it's forgotten in memory
    /// first, then on disk, so a file that won't delete can't leave this session linked; a 401 says so.
    /// </summary>
    void Link(LinkEvent e)
    {
        var before = _link;
        _link = before.Next(e);
        if (e is LinkEvent.Refused r && !before.IsCurrent(r.Token)) Log("401 for a token this PC no longer uses: ignored");
        if (before.Token is not null && _link.Token is null)
        {
            _api.Forget();
            try { _tokens.Clear(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"couldn't delete the device token file: {ex.Message}"); }
            if (e is LinkEvent.Refused)
                Notify("This PC was unlinked from your account. Link again from the tray menu; queued captures are kept.", ToolTipIcon.Warning);
        }
        RefreshMenu();
    }

    async Task CheckUpdates()
    {
        var check = _updater.CheckAsync(); // "checking…" from here, unless a check is already running
        RefreshMenu();
        await check;
        RefreshMenu();
        if (_autoUpdateItem.Checked) ApplyUpdateIfIdle();
    }

    /// <summary>
    /// With the opt-in on and an update downloaded: restart into it now, unless the game is running, an
    /// upload is mid-flight, or a tray window or link is open. Queued reads are files on disk and survive the
    /// restart. Called after each update check and on every retry tick, so a wait ends soon after the game
    /// closes; each wait is logged once.
    /// </summary>
    void ApplyUpdateIfIdle()
    {
        if (_updater.State.Ready is not { } v) return;
        var waitingFor = GameWindow.GameRunning() ? "the game to close"
            : _draining ? "the upload to finish"
            : DialogOpen ? "an Exiled Wealth window to close"
            : _link.Kind == LinkKind.Linking ? "the link to finish"
            : null;
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
        var menu = new ToolStripMenuItem($"Server: {_server.Host}");
        foreach (var url in ServerProfile.Servers)
        {
            var item = new ToolStripMenuItem(new Uri(url).Host) { Checked = new Uri(url) == _server.BaseUrl };
            item.Click += (_, _) => SwitchServer(url);
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    void SwitchServer(string url)
    {
        if (DialogOpen || !ServerProfile.Servers.Contains(url) || new Uri(url) == _server.BaseUrl) return;
        // Dev turns on diagnostics (screenshot uploads): nobody ends up there by curiosity without being told.
        if (url == ServerProfile.Dev && Modal(() => MessageBox.Show(
                "dev.exiledwealth.com is the developer's test server, not the live site. It has its own login, and your stash there starts empty.\n\n"
                + $"While this PC is linked to it, the read key ({_readKey}) also uploads a screenshot of the game window, "
                + "with the display and UI sections of the game's settings file, to help fix misreads.\n\n"
                + "Switch to the test server?",
                "Exiled Wealth", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)) != DialogResult.Yes)
            return;
        // server.txt first: whichever copy starts next reads it.
        try { Directory.CreateDirectory(DataDir); File.WriteAllText(_server.ChoiceFile, url); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"couldn't switch server: {e.Message}");
            Modal(() => MessageBox.Show($"Couldn't switch server: {e.Message}", "Exiled Wealth", MessageBoxButtons.OK, MessageBoxIcon.Warning));
            return;
        }
        // An update is downloaded: restart into it, and the new version starts on the new server. Quitting
        // instead would install it on exit while Program starts a fresh copy, two updaters against one exe.
        if (_updater.State.Ready is { } v)
        {
            Log($"server switched to {url}: restarting into {v}");
            _updater.RestartToApply(); // exits this process when it works
            Log("couldn't restart into the update: restarting without installing it");
            RestartRequested = true;
            ExitThread(); // not Quit(): installing on exit is the race above
            return;
        }
        Log($"server switched to {url}: restarting");
        RestartRequested = true;
        Quit();
    }

    void OnUi(Action a) { if (_ui is null) a(); else _ui.Post(_ => a(), null); }

    bool DialogOpen => _dialogs > 0;

    /// <summary>Shows a modal window, counted in <see cref="DialogOpen"/> while it's up.</summary>
    T Modal<T>(Func<T> show)
    {
        _dialogs++;
        try { return show(); }
        finally { _dialogs--; }
    }

    /// <summary>
    /// The read key: one screen copy of the game, read for the open tab. On dev the same copy is also the
    /// diagnostic screenshot, so a press never makes two copies (GGG: once per manual key press).
    /// </summary>
    void ReadNow()
    {
        // A PC that was unlinked keeps reading into its queue (the unlink notice promises queued reads are kept,
        // and they go up once it's linked again). One that has never loaded slot maps can't read anything, and
        // a press mid-link waits for the link: say so, and don't copy the screen for nothing.
        if (_link.Kind is LinkKind.Linking || (_link.Kind is LinkKind.Unlinked && !_tabs.Ready))
        { _lastRead = "Not linked: choose Link to account in the tray menu first"; RefreshMenu(); return; }
        // The last press is still in its tab-name OCR: say so, and don't copy the screen for nothing.
        if (_tabs.Busy) { _lastRead = "Still reading the last press: press again"; RefreshMenu(); return; }
        var diagnose = _server.IsDev && _link.Token is not null;
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
        // A second Read key window would dispose the current key twice and leak the one it registers.
        if (DialogOpen) return;
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
            Modal(() => dialog.ShowDialog());
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
        if (_link.Token is null) return;
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
        if (!_server.IsDev) return;

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
        // The token this drain sends with: a 401 unlinks only if it's still the current one (LinkState).
        if (_draining || _link.Token is not { } token || DateTime.UtcNow < _nextAttempt) return;
        _draining = true;
        var synced = false;
        try
        {
            // This drain takes everything waiting, so the batch ends here; a press during the request starts a new one.
            _batch.Taken();
            _debounce.Stop();
            int pending = _server.IsDev ? _queue.Pending().Count : 0, reads = _snapshots.Pending().Count;
            if (pending + reads == 0) return;
            var outcome = UploadOutcome.Stored;
            TimeSpan? retryAfter = null;
            if (reads > 0)
            {
                int sentReads;
                (sentReads, outcome, retryAfter) = await _snapshots.SyncAsync(_api);
                Log($"sync: {sentReads} of {reads} reads stored, {_snapshots.Pending().Count} still waiting ({outcome})");
                synced = outcome == UploadOutcome.Stored;
            }
            // Dev's screenshots: not after a 401 or 429 in this drain, and their own 401 or 429 counts like the sync's.
            if (pending > 0 && !outcome.StopsDrain())
            {
                var (sent, shots) = await _queue.DrainAsync(_api);
                Log($"drain: {sent} of {pending} uploaded, {_queue.Pending().Count} still waiting ({shots})");
                if (shots.StopsDrain()) outcome = shots;
            }
            switch (outcome)
            {
                case UploadOutcome.Unlinked: Link(new LinkEvent.Refused(token)); break; // the 401 said so; no second request to ask
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
        // A start-up that couldn't reach the server: this upload got through, so finish its check now.
        if (synced && _link.NeedsAccountCheck && await CheckAccount()) await LoadLayouts();
    }

    async Task LinkOrUnlink()
    {
        if (DialogOpen) return;
        if (_link.Kind is LinkKind.Checking or LinkKind.Linked)
        {
            if (Modal(() => MessageBox.Show("Unlink this PC from your Exiled Wealth account?", "Exiled Wealth",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question)) != DialogResult.Yes) return;
            Link(new LinkEvent.UnlinkRequested());
            return;
        }
        if (!_link.CanLink) return; // a link is already in progress (the item is disabled meanwhile)

        // Linking: from the code prompt until the server answers, one link at a time.
        Link(new LinkEvent.LinkStarted());
        try
        {
            var code = Modal(() => Prompt.Ask("Link to Exiled Wealth",
                "On the Exiled Wealth site, open the Scanner page and choose Get a link code. Type the code shown there:",
                upperCase: true));
            if (string.IsNullOrEmpty(code)) return;
            LinkResult result;
            try { result = await _api.LinkAsync(code, Environment.MachineName); }
            catch (Exception e) when (e is HttpRequestException or ScannerException or TaskCanceledException)
            {
                Modal(() => MessageBox.Show($"Couldn't link: {e.Message}", "Exiled Wealth", MessageBoxButtons.OK, MessageBoxIcon.Warning));
                return;
            }
            Link(new LinkEvent.LinkSucceeded(result.Token, result.AccountName));
            try
            {
                _tokens.Save(result.Token);
                Notify($"Linked to {result.AccountName}. Open a special tab in game and press {_readKey}: its item counts go to your account.", ToolTipIcon.Info);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
            {
                // Linked for this session (the token is in memory), but it won't survive a restart: say so.
                Log($"couldn't save the device token: {e.Message}");
                Modal(() => MessageBox.Show($"Linked to {result.AccountName}, but this PC couldn't save the link ({e.Message}). Reads upload until Exiled Wealth closes; after that, link again.",
                    "Exiled Wealth", MessageBoxButtons.OK, MessageBoxIcon.Warning));
            }
        }
        finally
        {
            // Cancelled, refused, or anything unexpected: never left at "Linking…".
            if (_link.Kind == LinkKind.Linking) Link(new LinkEvent.LinkFailed());
        }
        if (_link.Kind != LinkKind.Linked) return;
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

    void AskLabel()
    {
        if (DialogOpen) return;
        // Sticky until changed, so labelling never needs a dialog over the game.
        var label = Modal(() => Prompt.Ask("Capture label",
            "Describe what the next captures show, e.g. \"currency tab, big stacks\". Leave empty for none.",
            _label ?? ""));
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
        _accountItem.Text = _link.Kind switch
        {
            LinkKind.Linked => $"Linked: {_link.Account}",
            LinkKind.Checking => "Linked: (checking…)",
            _ => "Not linked",
        };
        _readItem.Text = $"Last read: {_lastRead}";
        _readKeyItem.Text = _hotkey.Registered ? $"Read key: {_readKey} (change…)" : $"Read key: {_readKey} is taken by another program (change…)";
        var update = _updater.State;
        _versionItem.Text = $"Version {Version}: {update.Status}";
        _updateItem.Text = update.Action;
        _updateItem.Enabled = update.ActionEnabled;
        _queueItem.Text = _server.IsDev
            ? $"Waiting to upload: {_snapshots.Pending().Count} reads, {_queue.Pending().Count} screenshots"
            : $"Waiting to upload: {_snapshots.Pending().Count} reads";
        var setAside = _snapshots.SetAsideCount();
        _setAsideItem.Text = $"Set aside: {setAside} reads";
        _setAsideItem.Visible = setAside > 0;
        _lastItem.Text = $"Last upload: {_lastUpload}";
        _labelItem.Text = _label is null ? "Set capture label…" : $"Label: {_label} (change…)";
        _linkItem.Text = _link.Kind switch
        {
            LinkKind.Linking => "Linking…",
            LinkKind.Unlinked => "Link to account…",
            _ => "Unlink this PC…",
        };
        _linkItem.Enabled = _link.Kind != LinkKind.Linking;
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
