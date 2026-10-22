using System.Net;
using EwCapture.Core;

namespace EwCapture.Tests;

public class UploadQueueTests
{
    static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1];

    static UploadQueue NewQueue() => new(Directory.CreateTempSubdirectory().FullName);

    [Fact]
    public void RejectedCapturesCanBePutBackInLine()
    {
        var q = NewQueue();
        var id = q.Enqueue(Png, "{}");
        q.Reject(id);
        Assert.Empty(q.Pending());
        Assert.Equal(1, q.RequeueRejected());
        Assert.Equal([id], q.Pending());
    }

    [Fact]
    public async Task CaptureFormQuotesPartNamesSoTheWorkerCanParseThem()
    {
        using var form = ScannerApi.CaptureForm(Png, "{}");
        var body = await form.ReadAsStringAsync();
        Assert.Contains("name=\"image\"; filename=\"capture.png\"", body);
        Assert.Contains("name=\"meta\"", body);
        Assert.DoesNotContain("name=image", body);
    }

    /// <summary>Answers each upload with the next status in the list and records the calls.</summary>
    sealed class ScriptedHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        readonly Queue<HttpStatusCode> _statuses = new(statuses);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(_statuses.Dequeue()) { Content = new StringContent("{}") });
        }
    }

    static (ScannerApi Api, ScriptedHandler Handler) Api(params HttpStatusCode[] statuses)
    {
        var handler = new ScriptedHandler(statuses);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://dev.example/") };
        return (new ScannerApi(http, "ewd_test"), handler);
    }

    [Fact]
    public void PendingIsOldestFirstAndSkipsHalfWrittenCaptures()
    {
        var q = NewQueue();
        var first = q.Enqueue(Png, "{}");
        var second = q.Enqueue(Png, "{}");
        File.WriteAllBytes(Path.Combine(q.Directory, "zzz-orphan.png"), Png); // no sidecar
        File.WriteAllBytes(Path.Combine(q.Directory, "zzz-partial.png.tmp"), Png);
        Assert.Equal([first, second], q.Pending());
    }

    [Fact]
    public async Task DrainUploadsAndRemovesStoredCaptures()
    {
        var q = NewQueue();
        q.Enqueue(Png, "{\"label\":\"a\"}");
        q.Enqueue(Png, "{\"label\":\"b\"}");
        var (api, handler) = Api(HttpStatusCode.Created, HttpStatusCode.Created);

        Assert.Equal(2, await q.DrainAsync(api));
        Assert.Empty(q.Pending());
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("Bearer ewd_test", r.Headers.Authorization?.ToString());
            Assert.Equal("/api/scanner/captures", r.RequestUri?.AbsolutePath);
        });
    }

    [Fact]
    public async Task DrainStopsAndKeepsEverythingWhenTheServerIsBusy()
    {
        var q = NewQueue();
        q.Enqueue(Png, "{}");
        q.Enqueue(Png, "{}");
        var (api, handler) = Api(HttpStatusCode.ServiceUnavailable);

        Assert.Equal(0, await q.DrainAsync(api));
        Assert.Equal(2, q.Pending().Count);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DrainKeepsCapturesWhenTheScannerWasUnlinked()
    {
        var q = NewQueue();
        q.Enqueue(Png, "{}");
        var (api, _) = Api(HttpStatusCode.Unauthorized);
        await q.DrainAsync(api);
        Assert.Single(q.Pending()); // they go up once the user links again
    }

    [Fact]
    public async Task DrainSetsAsideCapturesTheServerWillNeverAccept()
    {
        var q = NewQueue();
        q.Enqueue(Png, "{}");
        q.Enqueue(Png, "{}");
        var (api, _) = Api(HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.Created);
        Assert.Equal(1, await q.DrainAsync(api));
        Assert.Empty(q.Pending());
        Assert.Equal(2, Directory.GetFiles(q.RejectedDirectory).Length); // one png + json kept
    }
}
