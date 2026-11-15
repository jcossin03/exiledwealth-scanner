using System.Net;
using System.Text.Json;
using EwCapture.Core;
using EwCapture.Core.Recognition;

namespace EwCapture.Tests;

public class SnapshotTests
{
    static TabRead Read(params (int Slot, string? Item, int Count)[] reads) =>
        new(new TabMatch("Currency", 0.5234, 1, -1, 0.11), reads.Select(r => new SlotRead(r.Slot, r.Item, r.Count, 0.97)).ToList());

    static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MatchesTheShapeTheWorkerParses()
    {
        var s = Snapshot.From(Read((10, "Divine Orb", 150), (26, null, 5)), "Currency", "ew-capture 0.2.0", "0.5.5.4.2", T0);
        using var doc = JsonDocument.Parse(s.ToJson());
        var root = doc.RootElement;
        Assert.Equal("Currency", root.GetProperty("tab").GetProperty("layout").GetString());
        Assert.Equal("Currency", root.GetProperty("tab").GetProperty("name").GetString());
        Assert.Equal(0, root.GetProperty("tab").GetProperty("index").GetInt32());
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("capturedAt").GetString(), out _));
        var reads = root.GetProperty("reads");
        Assert.Equal(1, reads.GetArrayLength()); // open-slot reads aren't sent: their item is unknown
        Assert.Equal(10, reads[0].GetProperty("slot").GetInt32());
        Assert.Equal(150, reads[0].GetProperty("count").GetInt32());
        Assert.Equal("Divine Orb", reads[0].GetProperty("item").GetString());
    }

    [Fact]
    public async Task SyncSendsEveryWaitingReadInOneRequestWithAnEmptySeenList()
    {
        var q = new SnapshotQueue(Directory.CreateTempSubdirectory().FullName);
        var a = Snapshot.From(Read((10, "Divine Orb", 1)), "A", "t", "p", T0);
        q.Enqueue(a);
        q.Enqueue(a with { Tab = a.Tab with { Name = "B" } });
        q.Enqueue(Snapshot.From(Read((10, "Divine Orb", 5)), "A", "t", "p", T0.AddMinutes(1))); // replaces the first read of A
        var requests = new List<(string Path, JsonElement Body)>();
        var http = new HttpClient(new Handler(async r =>
        {
            requests.Add((r.RequestUri!.AbsolutePath, JsonDocument.Parse(await r.Content!.ReadAsStringAsync()).RootElement));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"league":"L","reads":[{"tabId":1},{"tabId":2}],"seen":1}""") };
        })) { BaseAddress = new Uri("https://dev.example/") };

        var (sent, outcome, _) = await q.SyncAsync(new ScannerApi(http, "ewd_test"));

        Assert.Equal((2, UploadOutcome.Stored), (sent, outcome));
        var (path, body) = Assert.Single(requests);
        Assert.Equal("/api/scanner/sync", path);
        var reads = body.GetProperty("reads").EnumerateArray().ToList();
        Assert.Equal(["B", "A"], reads.Select(r => r.GetProperty("name").GetString()));
        Assert.Equal("Currency", reads[1].GetProperty("layout").GetString());
        Assert.Equal(T0.AddMinutes(1), reads[1].GetProperty("at").GetDateTimeOffset());
        // Re-serialized, not the raw text: the body is indented, with \r\n on Windows.
        Assert.Equal("[[10,5]]", JsonSerializer.Serialize(reads[1].GetProperty("counts"))); // counts only: no item, no confidence
        Assert.False(reads[1].TryGetProperty("item", out _));
        // No seen notes since reads are key-press only, but the field stays: the site's parser expects an array.
        Assert.Equal(JsonValueKind.Array, body.GetProperty("seen").ValueKind);
        Assert.Equal(0, body.GetProperty("seen").GetArrayLength());
        Assert.Empty(q.Pending());
    }

    [Fact]
    public void ReadsLeftWaitingByAVersionThatReadOnATimerAreSetAside()
    {
        var q = new SnapshotQueue(Directory.CreateTempSubdirectory().FullName);
        var a = Snapshot.From(Read((10, "Divine Orb", 1)), "A", "ew-capture 0.4.13", "p", T0);
        q.Enqueue(a);
        q.Enqueue(a with { Tab = a.Tab with { Name = "B" }, Tool = "ew-capture 0.5.0" });
        q.Enqueue(a with { Tab = a.Tab with { Name = "C" }, Tool = "something else" });

        Assert.Equal(2, q.SetAsideOlderThan(new Version(0, 5, 0)));

        Assert.Single(q.Pending()); // only B, taken by 0.5.0
        Assert.Equal(2, Directory.GetFiles(q.RejectedDirectory).Length);
        Assert.Equal(0, q.SetAsideOlderThan(new Version(0, 5, 0)));
    }

    [Fact]
    public async Task SyncSetsAsideARefusedReadAndKeepsEverythingOnAServerError()
    {
        var q = new SnapshotQueue(Directory.CreateTempSubdirectory().FullName);
        var a = Snapshot.From(Read((10, "Divine Orb", 1)), "A", "t", "p", T0);
        q.Enqueue(a);
        q.Enqueue(a with { Tab = a.Tab with { Name = "B" } });
        var responses = new Queue<HttpResponseMessage>([
            new(HttpStatusCode.OK) { Content = new StringContent("""{"league":"L","reads":[{"tabId":1},{"error":"invalid read"}],"seen":0}""") },
            new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("""{"error":"later"}""") },
        ]);
        var http = new HttpClient(new Handler(_ => Task.FromResult(responses.Dequeue()))) { BaseAddress = new Uri("https://dev.example/") };
        var api = new ScannerApi(http, "ewd_test");

        Assert.Equal((1, UploadOutcome.Stored, (TimeSpan?)null), await q.SyncAsync(api));
        Assert.Empty(q.Pending()); // A stored, B set aside
        Assert.Single(Directory.GetFiles(q.RejectedDirectory));

        q.Enqueue(a with { Tab = a.Tab with { Name = "C" } });
        Assert.Equal((0, UploadOutcome.RetryLater, (TimeSpan?)null), await q.SyncAsync(api));
        Assert.Single(q.Pending()); // C waits for the server to come back
    }

    [Fact]
    public async Task SyncPassesTheServersRetryAfterUpAndReportsA401AsUnlinkedWithoutAskingAgain()
    {
        var q = new SnapshotQueue(Directory.CreateTempSubdirectory().FullName);
        q.Enqueue(Snapshot.From(Read((10, "Divine Orb", 1)), "A", "t", "p", T0));
        var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"error":"capacity"}""") };
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
        var responses = new Queue<HttpResponseMessage>([
            limited,
            new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down") },
            new(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"scanner not linked"}""") },
        ]);
        int calls = 0;
        var http = new HttpClient(new Handler(_ => { calls++; return Task.FromResult(responses.Dequeue()); })) { BaseAddress = new Uri("https://dev.example/") };
        var api = new ScannerApi(http, "ewd_test");

        Assert.Equal((0, UploadOutcome.RetryLater, (TimeSpan?)TimeSpan.FromSeconds(120)), await q.SyncAsync(api));
        Assert.Equal((0, UploadOutcome.RetryLater, (TimeSpan?)null), await q.SyncAsync(api)); // no header: the caller picks the wait
        Assert.Equal((0, UploadOutcome.Unlinked, (TimeSpan?)null), await q.SyncAsync(api));
        Assert.Equal(3, calls); // one request per sync: the 401 is the answer, no whoami to confirm it
        Assert.Single(q.Pending()); // the read is kept through all three
    }

    [Fact]
    public async Task SyncCopesWithAReadReplacedByANewerPressWhileItsRequestIsOut()
    {
        var q = new SnapshotQueue(Directory.CreateTempSubdirectory().FullName);
        var a = Snapshot.From(Read((10, "Divine Orb", 1)), "A", "t", "p", T0);
        q.Enqueue(a);
        q.Enqueue(a with { Tab = a.Tab with { Name = "B" } });
        q.Enqueue(a with { Tab = a.Tab with { Name = "C" } });
        var responses = new Queue<HttpResponseMessage>([
            // A is refused, B and C stored. A was replaced meanwhile, so its file is gone.
            new(HttpStatusCode.OK) { Content = new StringContent("""{"league":"L","reads":[{"error":"too many tabs for one league"},{"tabId":2},{"tabId":3}],"seen":0}""") },
            new(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"bad body"}""") },
        ]);
        var newer = 2;
        var http = new HttpClient(new Handler(_ =>
        {
            // The player reads tab A again while the request is out: Enqueue replaces A's waiting file.
            q.Enqueue(a with { CapturedAt = T0.AddMinutes(1), Reads = [new SnapshotRead(10, "Divine Orb", newer++, 0.97)] });
            return Task.FromResult(responses.Dequeue());
        })) { BaseAddress = new Uri("https://dev.example/") };
        var api = new ScannerApi(http, "ewd_test");

        // Per-read refusal: B and C are still deleted (before, setting A aside threw and they were sent again).
        Assert.Equal((2, UploadOutcome.Stored, (TimeSpan?)null), await q.SyncAsync(api));
        var waiting = Assert.Single(q.Pending()); // only the newer read of A
        Assert.Equal(2, JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(Path.Combine(q.Directory, waiting + ".json")), CaptureMeta.Json)!.Reads[0].Count);
        Assert.False(Directory.Exists(q.RejectedDirectory) && Directory.GetFiles(q.RejectedDirectory).Length > 0);

        // Whole body refused: the same replace during the request sets aside D (for its one more try), and keeps the newest A.
        q.Enqueue(a with { Tab = a.Tab with { Name = "D" } });
        Assert.Equal((0, UploadOutcome.Rejected, (TimeSpan?)null), await q.SyncAsync(api));
        Assert.Single(q.Pending());
        Assert.Single(Directory.GetFiles(q.WholeRejectedDirectory));
    }

    static ScannerApi Answering(Queue<HttpResponseMessage> responses, List<JsonElement>? bodies = null) =>
        new(new HttpClient(new Handler(async r =>
        {
            bodies?.Add(JsonDocument.Parse(await r.Content!.ReadAsStringAsync()).RootElement.Clone());
            return responses.Dequeue();
        })) { BaseAddress = new Uri("https://dev.example/") }, "ewd_test");

    static HttpResponseMessage Stored(int reads) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""{"league":"L","reads":[{{string.Join(",", Enumerable.Range(1, reads).Select(i => $$"""{"tabId":{{i}}}"""))}}],"seen":0}"""),
    };

    static HttpResponseMessage BadRequest() => new(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"body must be an object"}""") };

    static IEnumerable<string> Names(IEnumerable<JsonElement> bodies) =>
        bodies.SelectMany(b => b.GetProperty("reads").EnumerateArray()).Select(r => r.GetProperty("name").GetString()!);

    [Fact]
    public async Task AWholeBodyRefusalGetsOneMoreTryAfterTheNextStoredSync()
    {
        var q = new SnapshotQueue(Directory.CreateTempSubdirectory().FullName);
        var a = Snapshot.From(Read((10, "Divine Orb", 1)), "A", "t", "p", T0);
        q.Enqueue(a);
        q.Enqueue(a with { Tab = a.Tab with { Name = "B" } });
        var bodies = new List<JsonElement>();
        var api = Answering(new([BadRequest(), Stored(1), BadRequest()]), bodies);

        // A launch-day regression answers 400: A and B wait for one more try instead of being set aside for good.
        Assert.Equal((0, UploadOutcome.Rejected, (TimeSpan?)null), await q.SyncAsync(api));
        Assert.Empty(q.Pending());
        Assert.Equal(2, Directory.GetFiles(q.WholeRejectedDirectory).Length);
        Assert.Equal(2, q.SetAsideCount());

        // The next sync the server stores (a new read of C) puts them back in line.
        q.Enqueue(a with { Tab = a.Tab with { Name = "C" } });
        Assert.Equal((1, UploadOutcome.Stored, (TimeSpan?)null), await q.SyncAsync(api));
        Assert.Equal(2, q.Pending().Count);
        Assert.Empty(Directory.GetFiles(q.WholeRejectedDirectory));

        // Refused whole a second time: set aside for good, never retried again this session.
        Assert.Equal((0, UploadOutcome.Rejected, (TimeSpan?)null), await q.SyncAsync(api));
        Assert.Empty(q.Pending());
        Assert.Empty(Directory.GetFiles(q.WholeRejectedDirectory));
        Assert.Equal(2, Directory.GetFiles(q.RejectedDirectory).Length);
        Assert.Equal(2, q.SetAsideCount());
        Assert.Equal(["A", "B", "C", "A", "B"], Names(bodies));
    }

    [Fact]
    public async Task AReadRefusedOnItsOwnIsNotRetried()
    {
        var q = new SnapshotQueue(Directory.CreateTempSubdirectory().FullName);
        var a = Snapshot.From(Read((10, "Divine Orb", 1)), "A", "t", "p", T0);
        q.Enqueue(a);
        var refused = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"league":"L","reads":[{"error":"too many tabs for one league"}],"seen":0}""") };
        var api = Answering(new([refused, Stored(1)]));

        await q.SyncAsync(api);
        q.Enqueue(a with { Tab = a.Tab with { Name = "B" } });
        await q.SyncAsync(api); // stored: only whole-body refusals come back

        Assert.Empty(q.Pending());
        Assert.Single(Directory.GetFiles(q.RejectedDirectory));
        Assert.Equal(1, q.SetAsideCount());
    }

    [Fact]
    public async Task ARetryGivesWayToANewerReadOfTheSameTab()
    {
        var q = new SnapshotQueue(Directory.CreateTempSubdirectory().FullName);
        var a = Snapshot.From(Read((10, "Divine Orb", 1)), "A", "t", "p", T0);
        q.Enqueue(a);
        q.Enqueue(a with { Tab = a.Tab with { Name = "B" } });
        var responses = new Queue<HttpResponseMessage>([BadRequest(), Stored(1)]);
        var calls = 0;
        var api = new ScannerApi(new HttpClient(new Handler(_ =>
        {
            // A is read again while the stored sync (of C) is out, so a newer A is waiting when the retries come back.
            if (++calls == 2) q.Enqueue(a with { CapturedAt = T0.AddMinutes(5) });
            return Task.FromResult(responses.Dequeue());
        })) { BaseAddress = new Uri("https://dev.example/") }, "ewd_test");

        await q.SyncAsync(api); // A and B wait for one more try
        q.Enqueue(a with { Tab = a.Tab with { Name = "C" } });
        await q.SyncAsync(api);

        // B comes back; the old A doesn't, since the newer read of A goes up anyway (as Enqueue would have had it).
        var waiting = q.Pending().Select(id => JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(Path.Combine(q.Directory, id + ".json")), CaptureMeta.Json)!).ToList();
        Assert.Equal(["B", "A"], waiting.Select(s => s.Tab.Name));
        Assert.Equal(T0.AddMinutes(5), waiting[1].CapturedAt);
        Assert.Empty(Directory.GetFiles(q.WholeRejectedDirectory));
    }

    [Fact]
    public void SetAsideReadsAreDeletedAfterThirtyDays()
    {
        var q = new SnapshotQueue(Directory.CreateTempSubdirectory().FullName);
        Directory.CreateDirectory(q.WholeRejectedDirectory);
        var now = new DateTime(2026, 12, 11, 20, 0, 0, DateTimeKind.Utc);
        string File_(string dir, string name, int daysOld)
        {
            var path = Path.Combine(dir, name + ".json");
            File.WriteAllText(path, "{}");
            File.SetLastWriteTimeUtc(path, now.AddDays(-daysOld));
            return path;
        }
        var oldRefused = File_(q.RejectedDirectory, "old", 31);
        var newRefused = File_(q.RejectedDirectory, "new", 29);
        var oldWhole = File_(q.WholeRejectedDirectory, "old-whole", 45);
        var newWhole = File_(q.WholeRejectedDirectory, "new-whole", 1);

        Assert.Equal(2, q.DeleteSetAsideOlderThan(SnapshotQueue.KeepSetAside, now));

        Assert.False(File.Exists(oldRefused));
        Assert.False(File.Exists(oldWhole));
        Assert.True(File.Exists(newRefused));
        Assert.True(File.Exists(newWhole));
        Assert.Equal(2, q.SetAsideCount());
        Assert.Equal(0, new SnapshotQueue(Path.Combine(q.Directory, "none")).DeleteSetAsideOlderThan(SnapshotQueue.KeepSetAside, now));
    }

    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => f(r);
    }
}

public class TabNamerTests
{
    [Fact]
    public void TrustsTheStripOnlyWhenUncovered()
    {
        var n = new TabNamer();
        Assert.Null(n.Choose("Socketable/3", "Idols", stripUncovered: false, ocrName: "ts")); // nothing known yet: wait
        Assert.Equal("Sockets", n.Choose("Socketable/3", "Idols", true, "Sockets"));
        Assert.Equal("Sockets", n.Choose("Socketable/3", "Idols", false, "ts"));          // the misread under a tooltip
        Assert.Equal("Idols", n.Choose("Socketable/3", "Idols", true, null));            // OCR failed on a clear strip
        Assert.Null(n.Choose("Socketable/3", "Idols", false, "ts"));                     // two copies known: don't guess
    }
}
