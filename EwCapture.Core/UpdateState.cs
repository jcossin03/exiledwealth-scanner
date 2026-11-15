namespace EwCapture.Core;

public enum UpdatePhase
{
    /// <summary>A plain copied exe: Velopack can only update what it installed.</summary>
    NotInstalled,
    NotChecked,
    Checking,
    /// <summary>The last check finished: up to date, or it downloaded <see cref="UpdateState.Ready"/>.</summary>
    Checked,
    /// <summary>The last check failed (it's in the log).</summary>
    Failed,
}

/// <summary>
/// The update check as one value, so the tray's two update lines (the version line and the update item)
/// come from the same place and can't disagree. A downloaded update stays ready through later checks,
/// failed or not, until it's installed: the lines then both say it's ready.
/// </summary>
public sealed record UpdateState(UpdatePhase Phase, string? Ready = null, DateTime? CheckedAt = null)
{
    public static UpdateState Start(bool installed) => new(installed ? UpdatePhase.NotChecked : UpdatePhase.NotInstalled);

    /// <summary>Not installed, or a check is already running: a new one would do nothing.</summary>
    public bool CanCheck => Phase is not (UpdatePhase.NotInstalled or UpdatePhase.Checking);

    public UpdateState Checking() => CanCheck ? this with { Phase = UpdatePhase.Checking } : this;
    public UpdateState UpToDate(DateTime at) => this with { Phase = UpdatePhase.Checked, CheckedAt = at };
    public UpdateState Downloaded(string version, DateTime at) => new(UpdatePhase.Checked, version, at);
    public UpdateState Failed() => this with { Phase = UpdatePhase.Failed };

    /// <summary>After "Version X:" in the tray menu.</summary>
    public string Status => Phase == UpdatePhase.NotInstalled ? "updates need the installed app (see the Exiled Wealth site)"
        : Ready is { } v ? $"{v} ready: restart to update"
        : Phase switch
        {
            UpdatePhase.Checking => "checking…",
            UpdatePhase.Checked => $"up to date ({CheckedAt:HH:mm})",
            UpdatePhase.Failed => "update check failed (see log)",
            _ => "not checked yet",
        };

    /// <summary>The update item: install what's ready, else check.</summary>
    public string Action => Ready is { } v ? $"Restart to update to {v}" : "Check for updates";

    /// <summary>Clickable when it would do something: installing is always possible, checking only when <see cref="CanCheck"/>.</summary>
    public bool ActionEnabled => Ready is not null || CanCheck;
}
