using EwCapture.Core;

namespace EwCapture.Tests;

public class UploadBatchTests
{
    static readonly DateTime T0 = new(2026, 12, 11, 20, 0, 0, DateTimeKind.Utc);
    static DateTime At(double seconds) => T0.AddSeconds(seconds);
    static readonly DateTime NoWait = DateTime.MinValue;

    [Fact]
    public void ARetryTickDrainsWhenNoBatchIsCollecting()
    {
        var b = new UploadBatch();
        Assert.True(b.DrainNow(At(0), NoWait)); // reads left from a failed drain go on the next tick
    }

    [Fact]
    public void AReadWaitsThirtySecondsForTheNextPress()
    {
        var b = new UploadBatch();
        b.Add(At(0));
        Assert.False(b.DrainNow(At(0), NoWait));
        Assert.False(b.DrainNow(At(29), NoWait)); // the retry tick at 29 s leaves it alone
        Assert.True(b.DrainNow(At(30), NoWait));
    }

    [Fact]
    public void EachPressRestartsTheQuietSpell()
    {
        var b = new UploadBatch();
        b.Add(At(0));
        b.Add(At(20));
        b.Add(At(40));
        Assert.False(b.DrainNow(At(60), NoWait)); // 30 s after the first press, but only 20 s after the last
        Assert.True(b.DrainNow(At(70), NoWait));
    }

    [Fact]
    public void ALongSessionStillGoesUpEveryTwoMinutes()
    {
        var b = new UploadBatch();
        for (var s = 0; s < 120; s += 10) b.Add(At(s));
        Assert.False(b.DrainNow(At(119), NoWait));
        b.Add(At(120));
        Assert.True(b.DrainNow(At(120), NoWait)); // the press that reaches two minutes sends it
    }

    [Fact]
    public void AWaitTheServerAskedForHoldsEvenADueBatch()
    {
        var b = new UploadBatch();
        b.Add(At(0));
        var retryAfter = At(300);
        Assert.False(b.DrainNow(At(30), retryAfter));
        Assert.False(b.DrainNow(At(299), retryAfter));
        Assert.True(b.DrainNow(At(300), retryAfter));

        var idle = new UploadBatch();
        Assert.False(idle.DrainNow(At(60), retryAfter)); // a plain retry waits too
    }

    [Fact]
    public void ADrainTakesTheBatchAndAPressDuringItsRequestStartsANewOne()
    {
        var b = new UploadBatch();
        b.Add(At(0));
        b.Add(At(100));
        b.Taken();                               // the drain at 130 s sends both reads
        b.Add(At(131));                          // pressed while that request was out
        Assert.False(b.DrainNow(At(150), NoWait)); // a fresh 30 s, not the old batch's two minutes
        Assert.True(b.DrainNow(At(161), NoWait));
    }

    [Fact]
    public void AnOldBatchStartDoesNotSendTheNextPressAtOnce()
    {
        var b = new UploadBatch();
        b.Add(At(0));
        b.Taken();                                 // drained with nothing (or everything) waiting
        b.Add(At(600));
        Assert.False(b.DrainNow(At(600), NoWait)); // not "waited two minutes already"
    }
}
