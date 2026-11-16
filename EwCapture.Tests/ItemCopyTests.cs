using System.Net;
using System.Text.Json;
using EwCapture.Core;

namespace EwCapture.Tests;

public class ItemCopyTests
{
    // As the game copies them (patch 0.5.5, English client), captured on 2026-11-14.
    const string Tablet = "Item Class: Tablet\r\nRarity: Rare\r\nMythical Challenge\r\nIrradiated Tablet\r\n--------\r\nItem Level: 81\r\n--------\r\n{ Implicit Modifier }\r\nAdds Irradiated to a Map\r\n3 uses remaining\r\n--------\r\n{ Prefix Modifier \"Brimming\" (Tier: 1) }\r\nMap has 29(25-35)% increased number of Rare Monsters\r\n--------\r\nCan be used in a personal Map Device to add modifiers to a Map.\r\n";
    const string Waystone = "Item Class: Waystones\nRarity: Normal\nWaystone (Tier 15)\n--------\nRevives Available: 5\n--------\nItem Level: 79\n--------\nCan be used in a Map Device, allowing you to enter a Map. Waystones can only be used once.";

    static readonly DateTime T0 = new(2026, 12, 11, 20, 0, 0, DateTimeKind.Utc);
    static DateTime At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void AWaystoneOrTabletCopyIsAcceptedWithPlainLineEnds()
    {
        var tablet = ItemCopy.Accept(Tablet)!;
        Assert.StartsWith("Item Class: Tablet\nRarity: Rare\nMythical Challenge\nIrradiated Tablet\n--------\n", tablet);
        Assert.DoesNotContain('\r', tablet);
        Assert.EndsWith("add modifiers to a Map.", tablet);
        Assert.Equal(Waystone, ItemCopy.Accept(Waystone));
        Assert.Equal("Irradiated Tablet", ItemCopy.Label(tablet));
        Assert.Equal("Waystone (Tier 15)", ItemCopy.Label(Waystone));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hunter2")]
    [InlineData("https://example.com/some/link")]
    [InlineData("Item Class: Stackable Currency\nRarity: Currency\nDivine Orb\n--------\nStack Size: 3/10")]
    [InlineData("Item Class: Body Armours\nRarity: Rare\nDoom Shell\nVaal Regalia")]
    [InlineData("a note that mentions\nItem Class: Tablet\nRarity: Rare\nfurther down")]
    [InlineData("Item Class: Tablet\nRarity: Rare")]
    [InlineData("Item Class: Tablet\nRarity: Rare\nIrradiated\tTablet")]
    public void AnythingElseOnTheClipboardIsLeftAlone(string? clipboard) => Assert.Null(ItemCopy.Accept(clipboard));

    [Fact]
    public void ACopyTooLongToBeAnItemIsLeftAlone() =>
        Assert.Null(ItemCopy.Accept(Waystone + "\n" + new string('x', ItemCopy.MaxLength)));

    [Fact]
    public void TheClipboardIsReadOnlyWithEverythingInPlace()
    {
        bool Read(bool enabled = true, bool server = true, bool inFront = true, bool? owner = true) =>
            new ItemCopyGate().MayRead(enabled, server, inFront, owner, T0);
        Assert.True(Read());
        Assert.True(Read(owner: null)); // Windows names no owner: the game in front is what we have
        Assert.False(Read(enabled: false)); // the player hasn't turned it on
        Assert.False(Read(server: false)); // this server doesn't take copies
        Assert.False(Read(inFront: false)); // a copy made in another program
        Assert.False(Read(owner: false)); // another program set the clipboard while the game was in front
    }

    [Fact]
    public void OneCopyIsReadOnceHoweverOftenWindowsAnnouncesIt()
    {
        var gate = new ItemCopyGate();
        Assert.True(gate.MayRead(true, true, true, true, At(0)));
        Assert.False(gate.MayRead(true, true, true, true, At(0.05)));
        Assert.False(gate.MayRead(true, true, true, true, At(0.25)));
        Assert.True(gate.MayRead(true, true, true, true, At(0.4))); // the next press
        Assert.False(gate.MayRead(false, true, true, true, At(5))); // a refused change doesn't count as a read
    }

    [Fact]
    public void TheSameItemAgainRightAwayIsOneCopy()
    {
        var gate = new ItemCopyGate();
        Assert.Equal(Waystone, gate.Keep(Waystone, At(0)));
        Assert.Null(gate.Keep(Waystone, At(1))); // Ctrl+C held, or pressed twice
        Assert.Equal("other", gate.Keep("other", At(1.5)));
        Assert.Equal(Waystone, gate.Keep(Waystone, At(1.6))); // back to the first: a different item came between
        Assert.Equal(Waystone, gate.Keep(Waystone, At(10))); // copied again later: the site tells same from new
        Assert.Null(gate.Keep(null, At(20)));
    }

    static (ItemCopyQueue Queue, List<JsonElement> Bodies, ScannerApi Api) Setup(Func<int, HttpResponseMessage> answer)
    {
        var bodies = new List<JsonElement>();
        var http = new HttpClient(new Handler(async r =>
        {
            Assert.Equal("/api/scanner/items", r.RequestUri!.AbsolutePath);
            bodies.Add(JsonDocument.Parse(await r.Content!.ReadAsStringAsync()).RootElement);
            return answer(bodies.Count);
        })) { BaseAddress = new Uri("https://dev.example/") };
        return (new ItemCopyQueue(Directory.CreateTempSubdirectory().FullName), bodies, new ScannerApi(http, "ewd_test"));
    }

    static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    [Fact]
    public async Task WaitingCopiesGoUpOldestFirstAndAreDoneWithOnceAnswered()
    {
        var (q, bodies, api) = Setup(_ => Json(HttpStatusCode.OK, """{"league":"L","items":[{"stored":true},{"stored":false,"error":"not a waystone or tablet"}]}"""));
        var at = new DateTimeOffset(T0);
        q.Enqueue(at, ItemCopy.Accept(Tablet)!);
        q.Enqueue(at.AddSeconds(5), Waystone);

        var (sent, outcome, _, notTaken) = await q.SyncAsync(api);

        Assert.Equal((1, UploadOutcome.Stored), (sent, outcome));
        var items = Assert.Single(bodies).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.StartsWith("Item Class: Tablet\n", items[0].GetProperty("text").GetString());
        Assert.Equal(at, items[0].GetProperty("at").GetDateTimeOffset());
        Assert.Equal(Waystone, items[1].GetProperty("text").GetString());
        Assert.False(notTaken);
        Assert.Empty(q.Pending()); // the refused one too: the server will never take it
    }

    [Fact]
    public async Task ManyCopiesGoUpTwentyFiveARequest()
    {
        var (q, bodies, api) = Setup(n => Json(HttpStatusCode.OK, $$"""{"items":[{{string.Join(",", Enumerable.Repeat("""{"stored":true}""", n == 1 ? 25 : 5))}}]}"""));
        for (var i = 0; i < 30; i++) q.Enqueue(new DateTimeOffset(At(i)), Waystone);
        var (sent, outcome, _, notTaken) = await q.SyncAsync(api);
        Assert.Equal((30, UploadOutcome.Stored), (sent, outcome));
        Assert.Equal([25, 5], bodies.Select(b => b.GetProperty("items").GetArrayLength()));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, UploadOutcome.RetryLater)]
    [InlineData(HttpStatusCode.ServiceUnavailable, UploadOutcome.RetryLater)]
    [InlineData(HttpStatusCode.Unauthorized, UploadOutcome.Unlinked)]
    [InlineData((HttpStatusCode)426, UploadOutcome.RetryLater)]
    public async Task CopiesWaitWhenTheServerCantTakeThemNow(HttpStatusCode status, UploadOutcome expected)
    {
        var (q, _, api) = Setup(_ => Json(status, """{"error":"later"}"""));
        q.Enqueue(new DateTimeOffset(T0), Waystone);
        var (sent, outcome, _, notTaken) = await q.SyncAsync(api);
        Assert.Equal((0, expected), (sent, outcome));
        Assert.Single(q.Pending());
    }

    [Fact]
    public async Task AServerThatDoesntTakeCopiesGetsNoneAndNoneAreKept()
    {
        var (q, bodies, api) = Setup(_ => Json(HttpStatusCode.NotFound, """{"error":"not found"}"""));
        for (var i = 0; i < 30; i++) q.Enqueue(new DateTimeOffset(At(i)), Waystone);
        var (sent, outcome, _, notTaken) = await q.SyncAsync(api);
        Assert.Equal((0, UploadOutcome.Rejected), (sent, outcome));
        Assert.True(notTaken);
        Assert.Single(bodies); // one request was enough to learn it
        Assert.Empty(q.Pending());
    }

    [Fact]
    public void AnOfflinePcKeepsOnlyTheNewestCopies()
    {
        var q = new ItemCopyQueue(Directory.CreateTempSubdirectory().FullName);
        for (var i = 0; i < ItemCopyQueue.MaxWaiting + 5; i++) q.Enqueue(new DateTimeOffset(At(i)), Waystone);
        Assert.Equal(ItemCopyQueue.MaxWaiting, q.Pending().Count);
        Assert.Equal(ItemCopyQueue.MaxWaiting, q.Clear());
        Assert.Empty(q.Pending());
    }

    [Fact]
    public async Task WhoAmISaysWhetherTheServerTakesCopies()
    {
        ScannerApi Api(string body) => new(new HttpClient(new Handler(_ => Task.FromResult(Json(HttpStatusCode.OK, body)))) { BaseAddress = new Uri("https://dev.example/") }, "ewd_test");
        Assert.True((await Api("""{"accountName":"a#1","deviceId":1,"deviceName":"PC","mapItems":true}""").WhoAmIAsync())!.MapItems);
        Assert.False((await Api("""{"accountName":"a#1","deviceId":1,"deviceName":"PC"}""").WhoAmIAsync())!.MapItems); // the live site, for now
    }

    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => f(request);
    }
}
