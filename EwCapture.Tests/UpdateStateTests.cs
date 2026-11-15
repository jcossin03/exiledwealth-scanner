using EwCapture.Core;

namespace EwCapture.Tests;

public class UpdateStateTests
{
    static readonly DateTime At = new(2026, 12, 11, 20, 15, 0);

    [Fact]
    public void ACopiedExeNeverChecks()
    {
        var s = UpdateState.Start(installed: false);
        Assert.False(s.CanCheck);
        Assert.Same(s, s.Checking());
        Assert.Equal("updates need the installed app (see the Exiled Wealth site)", s.Status);
        Assert.False(s.ActionEnabled);
    }

    [Fact]
    public void ACheckGoesFromCheckingToUpToDate()
    {
        var s = UpdateState.Start(installed: true);
        Assert.Equal(("not checked yet", "Check for updates", true), (s.Status, s.Action, s.ActionEnabled));
        s = s.Checking();
        Assert.Equal(("checking…", false), (s.Status, s.ActionEnabled));
        s = s.UpToDate(At);
        Assert.Equal(("up to date (20:15)", "Check for updates"), (s.Status, s.Action));
    }

    [Fact]
    public void ASecondCheckWhileOneRunsChangesNothing()
    {
        var checking = UpdateState.Start(installed: true).Checking();
        Assert.Same(checking, checking.Checking()); // no "already checking" overwriting the first one's line
    }

    [Fact]
    public void ADownloadedUpdateStaysReadyThroughAFailedRecheck()
    {
        var s = UpdateState.Start(installed: true).Checking().Downloaded("0.5.4", At);
        Assert.Equal(("0.5.4 ready: restart to update", "Restart to update to 0.5.4"), (s.Status, s.Action));
        s = s.Checking();
        Assert.Equal("0.5.4 ready: restart to update", s.Status); // the item can still install it meanwhile
        Assert.True(s.ActionEnabled);
        s = s.Failed();
        // Before: "update check failed (see log)" next to "Restart to update to 0.5.4".
        Assert.Equal(("0.5.4 ready: restart to update", "Restart to update to 0.5.4"), (s.Status, s.Action));
    }

    [Fact]
    public void ANewerReleaseReplacesTheReadyOne()
    {
        var s = UpdateState.Start(true).Checking().Downloaded("0.5.4", At).Checking().Downloaded("0.5.5", At);
        Assert.Equal("Restart to update to 0.5.5", s.Action);
    }

    [Fact]
    public void AFailedFirstCheckSaysSo()
    {
        var s = UpdateState.Start(true).Checking().Failed();
        Assert.Equal(("update check failed (see log)", "Check for updates", true), (s.Status, s.Action, s.ActionEnabled));
    }
}
