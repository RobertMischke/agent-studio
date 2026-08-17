namespace AgentStudio.PublicDemo;

/// <summary>
/// Configuration of the public read-only demo edge (dossier AGT-W34 slice S4).
/// Every value is a ceiling, never a capability: raising a limit can only make
/// the edge more permissive inside the read-only allowlist, never unlock a
/// mutation or an execution path.
/// </summary>
public sealed class PublicDemoOptions
{
    public const string SectionName = "PublicDemo";

    /// <summary>
    /// Project identifiers a visitor may observe. Matches the ADR-0056 demo
    /// datastore; the edge applies it to REST, search, and SignalR alike.
    /// </summary>
    public List<string> Projects { get; set; } = ["demo-app", "demo-platform"];

    /// <summary>Sliding lifetime of an ephemeral viewer session in minutes.</summary>
    public int ViewerSessionMinutes { get; set; } = 30;

    /// <summary>Hard ceiling on concurrently tracked viewer sessions.</summary>
    public int MaxViewerSessions { get; set; } = 5000;

    /// <summary>
    /// Requests a single viewer may issue per rolling minute. The budget counts
    /// static assets too, so it has to admit a cold shell load plus a browsing
    /// burst; the S6 load probe is where this gets calibrated against real
    /// traffic rather than guessed.
    /// </summary>
    public int RequestsPerMinute { get; set; } = 600;

    /// <summary>
    /// Body ceiling for the edge. The read-only surface has no upload route, so
    /// this only has to admit SignalR negotiate and handshake payloads.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = 16 * 1024;

    public PublicDemoLimits ToLimits() => new(MaxRequestBodyBytes);
}
