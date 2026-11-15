namespace EwCapture.Core;

public enum LinkKind
{
    /// <summary>No device token: reads can't go anywhere until the player links.</summary>
    Unlinked,
    /// <summary>The link-code prompt is open or the link request is out. One link at a time.</summary>
    Linking,
    /// <summary>A token, but whose account it is hasn't been confirmed: at start-up, or after one that couldn't reach the server.</summary>
    Checking,
    /// <summary>A token and the account it belongs to.</summary>
    Linked,
}

/// <summary>
/// Whether this PC is linked to an account, as one value moved only by <see cref="Next"/>. It carries the
/// device token, so a 401 counts only for the token its request was sent with: the answer to a request sent
/// before an unlink and a fresh link must not throw the new link away. Saving or deleting the token file is
/// the caller's job; a token that couldn't be saved still links this session.
/// </summary>
public sealed record LinkState
{
    public LinkKind Kind { get; private init; }
    public string? Token { get; private init; }
    public string? Account { get; private init; }
    /// <summary>Checking, with a whoami out for <see cref="Token"/>: don't send a second.</summary>
    public bool Asking { get; private init; }

    LinkState() { }

    public static LinkState Unlinked => new();

    /// <summary>At start-up, from the token file (null when there's none or it's unreadable).</summary>
    public static LinkState FromToken(string? token) =>
        token is null ? Unlinked : new() { Kind = LinkKind.Checking, Token = token };

    /// <summary>"Link to account…" can start a link: only from unlinked, and never a second at once.</summary>
    public bool CanLink => Kind == LinkKind.Unlinked;

    /// <summary>After a stored upload: a check that couldn't reach the server can be finished now.</summary>
    public bool NeedsAccountCheck => Kind == LinkKind.Checking && !Asking;

    /// <summary>Whether a request sent with <paramref name="token"/> still speaks for this PC.</summary>
    public bool IsCurrent(string token) => Token is not null && Token == token;

    public LinkState Next(LinkEvent e) => e switch
    {
        LinkEvent.AskingWho when NeedsAccountCheck => this with { Asking = true },
        LinkEvent.WhoAmI w when IsCurrent(w.Token) && Kind is LinkKind.Checking or LinkKind.Linked =>
            new() { Kind = LinkKind.Linked, Token = Token, Account = w.Account },
        LinkEvent.WhoAmIFailed f when IsCurrent(f.Token) && Kind == LinkKind.Checking => this with { Asking = false },
        LinkEvent.LinkStarted when CanLink => new() { Kind = LinkKind.Linking },
        LinkEvent.LinkFailed when Kind == LinkKind.Linking => Unlinked,
        LinkEvent.LinkSucceeded s when Kind == LinkKind.Linking => new() { Kind = LinkKind.Linked, Token = s.Token, Account = s.Account },
        LinkEvent.Refused r when IsCurrent(r.Token) => Unlinked,
        LinkEvent.UnlinkRequested when Kind is LinkKind.Checking or LinkKind.Linked => Unlinked,
        _ => this,
    };
}

/// <summary>What can happen to the link. Anything that doesn't fit the current state leaves it as it is.</summary>
public abstract record LinkEvent
{
    /// <summary>A whoami is going out for the current token (start-up, or after a stored upload).</summary>
    public sealed record AskingWho : LinkEvent;
    /// <summary>The whoami for <paramref name="Token"/> answered: it belongs to <paramref name="Account"/>.</summary>
    public sealed record WhoAmI(string Token, string Account) : LinkEvent;
    /// <summary>No answer (offline, a timeout, a server error): still checking, and the next stored upload asks again.</summary>
    public sealed record WhoAmIFailed(string Token) : LinkEvent;
    public sealed record LinkStarted : LinkEvent;
    /// <summary>The prompt was cancelled or the server refused the code.</summary>
    public sealed record LinkFailed : LinkEvent;
    public sealed record LinkSucceeded(string Token, string Account) : LinkEvent;
    /// <summary>A 401 for a request (upload or whoami) sent with <paramref name="Token"/>.</summary>
    public sealed record Refused(string Token) : LinkEvent;
    /// <summary>The player chose "Unlink this PC…".</summary>
    public sealed record UnlinkRequested : LinkEvent;
}
