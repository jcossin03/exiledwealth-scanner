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
    public void OnlyChangedTabsAreSent()
    {
        var t = new ChangeTracker();
        var a = Snapshot.From(Read((10, "Divine Orb", 150)), "Currency", "t", "p", T0);
        Assert.True(t.ShouldSend(a));
        t.MarkSent(a);
        Assert.False(t.ShouldSend(a with { CapturedAt = T0.AddMinutes(5) }));
        Assert.True(t.ShouldSend(Snapshot.From(Read((10, "Divine Orb", 151)), "Currency", "t", "p", T0.AddMinutes(5))));
        Assert.True(t.ShouldSend(Snapshot.From(Read((10, "Divine Orb", 150)), "Currency 2", "t", "p", T0.AddMinutes(5)))); // another copy
        Assert.False(t.ShouldSend(a with { CapturedAt = T0.AddDays(3) })); // unchanged stays unsent however long: the seen note covers it
    }

    [Fact]
    public void UnchangedTabsAreNotedAsSeenAtMostEveryFewMinutes()
    {
        var t = new SeenTracker(TimeSpan.FromMinutes(2));
        var a = Snapshot.From(Read((10, "Divine Orb", 150)), "Currency", "t", "p", T0);
        Assert.Empty(t.TakeNotes());
        t.Note(a);
        t.Note(a with { CapturedAt = T0.AddMinutes(1) }); // too soon: still the first note
        var note = Assert.Single(t.TakeNotes());
        Assert.Equal(new SeenNote("Currency", "Currency", 0, T0), note);
        Assert.Empty(t.TakeNotes()); // taking clears
        t.Note(a with { CapturedAt = T0.AddMinutes(3) });
        Assert.Single(t.TakeNotes());
    }

    [Fact]
    public async Task SyncSendsEveryWaitingReadAndTheSeenNotesInOneRequest()
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

        var (sent, outcome, _) = await q.SyncAsync(new ScannerApi(http, "ewd_test"), [new SeenNote("Currency", "C", 0, T0)]);

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
        Assert.Equal("C", Assert.Single(body.GetProperty("seen").EnumerateArray()).GetProperty("name").GetString());
        Assert.Empty(q.Pending());
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

        Assert.Equal((1, UploadOutcome.Stored, (TimeSpan?)null), await q.SyncAsync(api, []));
        Assert.Empty(q.Pending()); // A stored, B set aside
        Assert.Single(Directory.GetFiles(q.RejectedDirectory));

        q.Enqueue(a with { Tab = a.Tab with { Name = "C" } });
        Assert.Equal((0, UploadOutcome.RetryLater, (TimeSpan?)null), await q.SyncAsync(api, []));
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

        Assert.Equal((0, UploadOutcome.RetryLater, (TimeSpan?)TimeSpan.FromSeconds(120)), await q.SyncAsync(api, []));
        Assert.Equal((0, UploadOutcome.RetryLater, (TimeSpan?)null), await q.SyncAsync(api, [])); // no header: the caller picks the wait
        Assert.Equal((0, UploadOutcome.Unlinked, (TimeSpan?)null), await q.SyncAsync(api, []));
        Assert.Equal(3, calls); // one request per sync: the 401 is the answer, no whoami to confirm it
        Assert.Single(q.Pending()); // the read is kept through all three
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
