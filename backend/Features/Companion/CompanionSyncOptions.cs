namespace AgentStudio.Companion;

/// <summary>
/// Bound from the <c>Companion</c> section of <c>appsettings*.json</c>. Default
/// disabled so a fresh checkout never tries to phone home. The full design is
/// documented in <c>docs/product/companion-app-design.md</c> and ADR-0018.
/// </summary>
public sealed class CompanionSyncOptions
{
    public const string SectionName = "Companion";

    /// <summary>Master switch. The HostedService is registered unconditionally but exits its loop immediately when this is false.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Relay base URL, e.g. https://my-companion-relay.fly.dev. No trailing slash.</summary>
    public string RelayUrl { get; set; } = "";

    /// <summary>Shared bearer token. Sent as <c>Authorization: Bearer ...</c> on every relay call.</summary>
    public string Token { get; set; } = "";

    /// <summary>Tick cadence in seconds. Floor 5, ceiling 60. Default 10.</summary>
    public int SyncIntervalSeconds { get; set; } = 10;

    /// <summary>Free-form host label that appears in the snapshot (e.g. "rmisc-desktop").</summary>
    public string HostName { get; set; } = "";

    /// <summary>Surface the dev banner state on the phone so the user knows whether they are looking at the dev or stable checkout.</summary>
    public bool IsDev { get; set; } = false;

    public int ResolvedIntervalSeconds() => Math.Clamp(SyncIntervalSeconds, 5, 60);
}
