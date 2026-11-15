namespace EwCapture.Core;

/// <summary>
/// When waiting reads go up. A read waits for the next press: the batch is due once the presses
/// stop for <see cref="Quiet"/>, or once its first read has waited <see cref="Max"/>, so reading ten
/// tabs in a row is one request, not ten. A wait the server asked for (Retry-After, or the doubling
/// backoff after a failure) holds every drain, due or not. This only decides; the caller's timers
/// wake it, and it is used from one thread (the tray app's UI thread).
/// </summary>
public sealed class UploadBatch
{
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(30), Max = TimeSpan.FromMinutes(2);

    DateTime? _start, _lastRead;

    /// <summary>A read was queued at <paramref name="now"/>: it joins the batch, or starts one.</summary>
    public void Add(DateTime now)
    {
        _start ??= now;
        _lastRead = now;
    }

    /// <summary>A drain is taking everything waiting: the next read starts a new batch.</summary>
    public void Taken() => _start = _lastRead = null;

    /// <summary>
    /// Whether to drain at <paramref name="now"/>: never before <paramref name="nextAttempt"/>, and
    /// not while a batch is still collecting. With no batch open (a retry of a failed drain) it's yes.
    /// </summary>
    public bool DrainNow(DateTime now, DateTime nextAttempt) =>
        now >= nextAttempt && (_start is null || now - _lastRead >= Quiet || now - _start >= Max);
}
