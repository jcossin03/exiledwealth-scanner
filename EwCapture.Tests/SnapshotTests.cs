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
        Assert.Null(t.Take());
        t.Note(a);
        t.Note(a with { CapturedAt = T0.AddMinutes(1) }); // too soon: still the first note
        using (var doc = System.Text.Json.JsonDocument.Parse(t.Take()!))
        {
            var tab = Assert.Single(doc.RootElement.GetProperty("tabs").EnumerateArray());
            Assert.Equal("Currency", tab.GetProperty("layout").GetString());
            Assert.Equal("Currency", tab.GetProperty("name").GetString());
            Assert.Equal(T0, tab.GetProperty("at").GetDateTimeOffset());
        }
        Assert.Null(t.Take()); // taking clears
        t.Note(a with { CapturedAt = T0.AddMinutes(3) });
        Assert.NotNull(t.Take());
    }

    [Fact]
    public async Task QueueUploadsInOrderAndSetsAsideRefusedOnes()
    {
        var q = new SnapshotQueue(Directory.CreateTempSubdirectory().FullName);
        var first = Snapshot.From(Read((10, "Divine Orb", 1)), "A", "t", "p", T0);
        q.Enqueue(first);
        q.Enqueue(first with { Tab = first.Tab with { Name = "B" } });
        q.Enqueue(first with { Tab = first.Tab with { Name = "C" } });
        var bodies = new List<string>();
        var codes = new Queue<HttpStatusCode>([HttpStatusCode.Created, HttpStatusCode.BadRequest, HttpStatusCode.ServiceUnavailable]);
        var http = new HttpClient(new Handler(async r => { bodies.Add(await r.Content!.ReadAsStringAsync()); return new HttpResponseMessage(codes.Dequeue()); }))
            { BaseAddress = new Uri("https://dev.example/") };

        Assert.Equal(1, await q.DrainAsync(new ScannerApi(http, "ewd_test")));

        Assert.Equal(["\"A\"", "\"B\"", "\"C\""], bodies.Select(b => JsonDocument.Parse(b).RootElement.GetProperty("tab").GetProperty("name").GetRawText()));
        Assert.Single(q.Pending()); // C waits for the server to come back
        Assert.Single(Directory.GetFiles(q.RejectedDirectory)); // B is kept to look at
    }

    [Fact]
    public async Task QueueKeepsOnlyTheLatestReadOfEachTab()
    {
        var q = new SnapshotQueue(Directory.CreateTempSubdirectory().FullName);
        var a = Snapshot.From(Read((10, "Divine Orb", 1)), "A", "t", "p", T0);
        q.Enqueue(a);
        q.Enqueue(a with { Tab = a.Tab with { Name = "B" } });
        q.Enqueue(Snapshot.From(Read((10, "Divine Orb", 5)), "A", "t", "p", T0.AddMinutes(1)));
        Assert.Equal(2, q.Pending().Count);

        var bodies = new List<JsonElement>();
        var http = new HttpClient(new Handler(async r => { bodies.Add(JsonDocument.Parse(await r.Content!.ReadAsStringAsync()).RootElement); return new HttpResponseMessage(HttpStatusCode.Created); }))
            { BaseAddress = new Uri("https://dev.example/") };
        Assert.Equal(2, await q.DrainAsync(new ScannerApi(http, "ewd_test")));
        Assert.Equal(["B", "A"], bodies.Select(b => b.GetProperty("tab").GetProperty("name").GetString()));
        Assert.Equal(5, bodies[1].GetProperty("reads")[0].GetProperty("count").GetInt32()); // the newer read of A, not the first
        Assert.Empty(q.Pending());
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
