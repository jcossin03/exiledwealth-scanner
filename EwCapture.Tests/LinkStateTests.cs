using EwCapture.Core;

namespace EwCapture.Tests;

public class LinkStateTests
{
    static LinkState Linked(string token = "t1", string account = "Exile#1") =>
        LinkState.Unlinked.Next(new LinkEvent.LinkStarted()).Next(new LinkEvent.LinkSucceeded(token, account));

    [Fact]
    public void StartsFromTheTokenFile()
    {
        Assert.Equal(LinkKind.Unlinked, LinkState.FromToken(null).Kind);
        var s = LinkState.FromToken("t1");
        Assert.Equal((LinkKind.Checking, "t1", null, false), (s.Kind, s.Token, s.Account, s.Asking));
    }

    [Fact]
    public void AStartUpCheckThatAnswersLinksTheAccount()
    {
        var s = LinkState.FromToken("t1").Next(new LinkEvent.AskingWho());
        Assert.True(s.Asking);
        Assert.False(s.NeedsAccountCheck); // one whoami at a time
        s = s.Next(new LinkEvent.WhoAmI("t1", "Exile#1"));
        Assert.Equal((LinkKind.Linked, "t1", "Exile#1"), (s.Kind, s.Token, s.Account));
    }

    [Fact]
    public void AStartUpCheckThatCantReachTheServerIsFinishedAfterTheNextStoredUpload()
    {
        var s = LinkState.FromToken("t1").Next(new LinkEvent.AskingWho()).Next(new LinkEvent.WhoAmIFailed("t1"));
        Assert.Equal(LinkKind.Checking, s.Kind);
        Assert.True(s.NeedsAccountCheck); // the caller asks again after an upload gets through, not on a timer
        Assert.Equal(LinkKind.Linked, s.Next(new LinkEvent.AskingWho()).Next(new LinkEvent.WhoAmI("t1", "Exile#1")).Kind);
        Assert.False(Linked().NeedsAccountCheck);
        Assert.False(LinkState.Unlinked.NeedsAccountCheck);
    }

    [Fact]
    public void A401UnlinksOnlyForTheTokenItsRequestUsed()
    {
        Assert.Equal(LinkKind.Unlinked, Linked("t1").Next(new LinkEvent.Refused("t1")).Kind);
        Assert.Equal(LinkKind.Unlinked, LinkState.FromToken("t1").Next(new LinkEvent.Refused("t1")).Kind); // start-up whoami's 401

        // Sent with t1, answered after the player unlinked and linked again with t2: t2 stays.
        var relinked = Linked("t2");
        Assert.Same(relinked, relinked.Next(new LinkEvent.Refused("t1")));
        // Answered while the new link is still in progress: nothing to unlink.
        var linking = LinkState.Unlinked.Next(new LinkEvent.LinkStarted());
        Assert.Same(linking, linking.Next(new LinkEvent.Refused("t1")));
        // A late whoami for the old token doesn't touch the new link either.
        Assert.Same(relinked, relinked.Next(new LinkEvent.WhoAmI("t1", "Someone#2")));
    }

    [Fact]
    public void OneLinkAtATime()
    {
        var linking = LinkState.Unlinked.Next(new LinkEvent.LinkStarted());
        Assert.Equal(LinkKind.Linking, linking.Kind);
        Assert.False(linking.CanLink);
        Assert.Same(linking, linking.Next(new LinkEvent.LinkStarted()));
        Assert.Same(linking, linking.Next(new LinkEvent.UnlinkRequested()));
        Assert.False(Linked().CanLink);
        Assert.Equal(LinkKind.Unlinked, linking.Next(new LinkEvent.LinkFailed()).Kind); // cancelled or refused: link again
    }

    [Fact]
    public void ALinkTakesTheTokenAndAccountTheServerGave()
    {
        var s = Linked("t9", "Exile#9");
        Assert.Equal((LinkKind.Linked, "t9", "Exile#9"), (s.Kind, s.Token, s.Account));
        // A link answer that doesn't belong to a link in progress changes nothing.
        Assert.Equal(LinkKind.Unlinked, LinkState.Unlinked.Next(new LinkEvent.LinkSucceeded("t9", "Exile#9")).Kind);
        var checking = LinkState.FromToken("t1");
        Assert.Same(checking, checking.Next(new LinkEvent.LinkSucceeded("t9", "Exile#9")));
    }

    [Fact]
    public void ThePlayerCanUnlinkWhetherOrNotTheAccountIsConfirmed()
    {
        Assert.Equal(LinkKind.Unlinked, Linked().Next(new LinkEvent.UnlinkRequested()).Kind);
        Assert.Equal(LinkKind.Unlinked, LinkState.FromToken("t1").Next(new LinkEvent.UnlinkRequested()).Kind);
        Assert.Null(Linked().Next(new LinkEvent.UnlinkRequested()).Token);
    }
}
