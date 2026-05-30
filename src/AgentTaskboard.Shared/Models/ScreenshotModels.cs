namespace OrchestratorApi.Models;

/// <summary>
/// Wire shape for one screenshot file as surfaced by the per-job and
/// workspace screenshot listings. <see cref="LocalPath"/> is for an
/// "open in Explorer" affordance; the URL is the routable form the
/// frontend renders.
/// </summary>
public record JobScreenshot
{
    public string JobId { get; init; } = "";
    public string JobTitle { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string WatchPath { get; init; } = "";

    public string FileName { get; init; } = "";
    /// <summary>Always begins with <c>results/</c>. Useful for captioning and protocol cross-reference.</summary>
    public string RelativePath { get; init; } = "";
    /// <summary>Routable URL that serves this file (sub-path aware).</summary>
    public string Url { get; init; } = "";
    /// <summary>Short human caption, typically the Playwright spec name or parent folder.</summary>
    public string Caption { get; init; } = "";
    /// <summary>One of <c>passed</c>, <c>failed</c>, <c>skipped</c>, <c>unknown</c>, or null when no harvest index applies.</summary>
    public string? Status { get; init; }
    /// <summary>Absolute on-disk path. The frontend offers a copy-to-clipboard / open-in-Explorer affordance off this.</summary>
    public string LocalPath { get; init; } = "";
    public DateTime TimestampUtc { get; init; }
}

/// <summary>
/// Wire shape for <c>GET /api/jobs/{id}/screenshots</c>.
/// </summary>
public record JobScreenshotsResponse
{
    public string JobId { get; init; } = "";
    public List<JobScreenshot> Screenshots { get; init; } = [];
}

/// <summary>
/// Wire shape for <c>GET /api/workspace/screenshots</c>. Items are
/// already ordered newest-first; the frontend re-buckets by hour for
/// the reel.
/// </summary>
public record WorkspaceScreenshotsResponse
{
    public int WindowHours { get; init; }
    public string? ProjectFilter { get; init; }
    public List<JobScreenshot> Screenshots { get; init; } = [];
}
