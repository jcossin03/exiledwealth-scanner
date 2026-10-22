using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace EwCapture.Core;

public sealed record LinkResult(string Token, int DeviceId, string AccountName);

public sealed record WhoAmI(string AccountName, int DeviceId, string DeviceName);

public enum UploadOutcome
{
    Stored,
    /// <summary>The server will never accept this file (bad format, too large).</summary>
    Rejected,
    /// <summary>Token revoked or unknown: the user needs to link again.</summary>
    Unlinked,
    /// <summary>Rate limit, server error or no connection: try again later.</summary>
    RetryLater,
}

public sealed class ScannerException(string message) : Exception(message);

/// <summary>Client for the /api/scanner/* routes on the Exiled Wealth Worker.</summary>
public sealed class ScannerApi(HttpClient http, string? token = null)
{
    public string? Token { get; private set; } = token;

    /// <summary>Optional sink for one line per upload, so failures can be diagnosed.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Drop the token after an unlink; the next call needs a fresh link.</summary>
    public void Forget() => Token = null;

    public async Task<LinkResult> LinkAsync(string code, string deviceName, CancellationToken ct = default)
    {
        using var res = await http.PostAsJsonAsync("api/scanner/link", new { code, deviceName }, ct);
        if (!res.IsSuccessStatusCode) throw new ScannerException(await ErrorText(res, ct));
        var body = await res.Content.ReadFromJsonAsync<LinkResult>(CaptureMeta.Json, ct)
            ?? throw new ScannerException("empty response");
        Token = body.Token;
        return body;
    }

    /// <summary>Who the token belongs to, or null if it no longer works.</summary>
    public async Task<WhoAmI?> WhoAmIAsync(CancellationToken ct = default)
    {
        using var req = Authed(HttpMethod.Get, "api/scanner/whoami");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) return null;
        if (!res.IsSuccessStatusCode) throw new ScannerException(await ErrorText(res, ct));
        return await res.Content.ReadFromJsonAsync<WhoAmI>(CaptureMeta.Json, ct);
    }

    public async Task<UploadOutcome> UploadCaptureAsync(byte[] png, string metaJson, CancellationToken ct = default)
    {
        using var form = CaptureForm(png, metaJson);
        using var req = Authed(HttpMethod.Post, "api/scanner/captures");
        req.Content = form;
        return await Send(req, $"{png.Length:N0} bytes", ct);
    }

    /// <summary>One tab's read (counts only, never pixels).</summary>
    public async Task<UploadOutcome> UploadSnapshotAsync(string snapshotJson, CancellationToken ct = default)
    {
        using var req = Authed(HttpMethod.Post, "api/scanner/snapshots");
        req.Content = new StringContent(snapshotJson, System.Text.Encoding.UTF8, "application/json");
        return await Send(req, "snapshot", ct);
    }

    /// <summary>Tabs seen unchanged (see <see cref="SeenTracker"/>).</summary>
    public async Task<UploadOutcome> SendSeenAsync(string seenJson, CancellationToken ct = default)
    {
        using var req = Authed(HttpMethod.Post, "api/scanner/seen");
        req.Content = new StringContent(seenJson, System.Text.Encoding.UTF8, "application/json");
        return await Send(req, "seen", ct);
    }

    /// <summary>
    /// The slot layouts, or null when the copy we hold (its version as ETag) is still current.
    /// </summary>
    public async Task<(string Json, string Version)?> GetLayoutsAsync(string? haveVersion, CancellationToken ct = default)
    {
        using var req = Authed(HttpMethod.Get, "api/scanner/layouts");
        if (haveVersion is not null) req.Headers.TryAddWithoutValidation("If-None-Match", $"\"{haveVersion}\"");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotModified) return null;
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new ScannerException("scanner is not linked");
        if (!res.IsSuccessStatusCode) throw new ScannerException(await ErrorText(res, ct));
        var json = await res.Content.ReadAsStringAsync(ct);
        return (json, res.Headers.ETag?.Tag.Trim('"') ?? "");
    }

    async Task<UploadOutcome> Send(HttpRequestMessage req, string what, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (HttpRequestException e) { Log?.Invoke($"upload failed to connect: {e.Message}"); return UploadOutcome.RetryLater; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { Log?.Invoke("upload timed out"); return UploadOutcome.RetryLater; }
        using (res)
        {
            var status = (int)res.StatusCode;
            Log?.Invoke(res.IsSuccessStatusCode
                ? $"upload {status} ({what})"
                : $"upload {status} ({what}): {await ErrorText(res, ct)}");
            return status switch
            {
                >= 200 and < 300 => UploadOutcome.Stored,
                401 => UploadOutcome.Unlinked,
                400 or 413 or 415 => UploadOutcome.Rejected,
                _ => UploadOutcome.RetryLater,
            };
        }
    }

    /// <summary>
    /// The upload body. Part names are quoted by hand: .NET writes name=image bare,
    /// and the Workers form parser silently drops such parts ("missing image").
    /// </summary>
    public static MultipartFormDataContent CaptureForm(byte[] png, string metaJson)
    {
        var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(png);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        image.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            { Name = "\"image\"", FileName = "\"capture.png\"" };
        form.Add(image);
        var meta = new StringContent(metaJson);
        meta.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"meta\"" };
        form.Add(meta);
        return form;
    }

    HttpRequestMessage Authed(HttpMethod method, string path)
    {
        if (Token is null) throw new ScannerException("scanner is not linked");
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return req;
    }

    static async Task<string> ErrorText(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() is { } msg) return msg;
        }
        catch (JsonException) { }
        return $"server returned {(int)res.StatusCode}";
    }
}
