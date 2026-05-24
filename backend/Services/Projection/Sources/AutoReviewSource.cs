using System.Globalization;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Markdown;

namespace OrchestratorApi.Services.Projection.Sources;

/// <summary>
/// Projects per-aspect verdict files (<c>aspect-*.md</c>) into structured
/// <c>workbench.aspectVerdict</c> events so the chat surfaces an automated
/// reviewer's verdict next to the human conversation. Reads the frontmatter
/// (aspect / status / summary / created_at) plus the markdown body.
/// </summary>
public sealed class AutoReviewSource : IConversationEventSource
{
    public string SourceKind => "auto-review";

    public Task<IReadOnlyList<RawSourceEvent>> ReadAsync(JobInfo jobInfo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobInfo.FolderPath) || !Directory.Exists(jobInfo.FolderPath))
        {
            return Task.FromResult<IReadOnlyList<RawSourceEvent>>(Array.Empty<RawSourceEvent>());
        }

        IReadOnlyList<RawSourceEvent> events;
        try
        {
            var files = Directory.GetFiles(jobInfo.FolderPath, "aspect-*.md", SearchOption.TopDirectoryOnly);
            var list = new List<RawSourceEvent>(files.Length);
            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                var ev = TryProject(path);
                if (ev is not null) list.Add(ev);
            }
            events = list;
        }
        catch (IOException)
        {
            events = Array.Empty<RawSourceEvent>();
        }
        return Task.FromResult(events);
    }

    public DateTime GetSourceMTimeUtc(JobInfo jobInfo)
    {
        if (string.IsNullOrWhiteSpace(jobInfo.FolderPath) || !Directory.Exists(jobInfo.FolderPath))
        {
            return DateTime.MinValue;
        }
        try
        {
            DateTime newest = DateTime.MinValue;
            foreach (var path in Directory.GetFiles(jobInfo.FolderPath, "aspect-*.md", SearchOption.TopDirectoryOnly))
            {
                var t = File.GetLastWriteTimeUtc(path);
                if (t > newest) newest = t;
            }
            return newest;
        }
        catch { return DateTime.MinValue; }
    }

    private static RawSourceEvent? TryProject(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch { return null; }

        var fm = FrontmatterParser.Parse(text);
        var aspect = fm.Fields.TryGetValue("aspect", out var a) ? a : Path.GetFileNameWithoutExtension(path);
        var status = fm.Fields.TryGetValue("status", out var s) ? s : "unknown";
        var summary = fm.Fields.TryGetValue("summary", out var sm) ? sm : null;
        DateTime ts;
        if (!fm.Fields.TryGetValue("created_at", out var created)
            || !DateTime.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out ts))
        {
            try { ts = File.GetLastWriteTimeUtc(path); } catch { ts = DateTime.UtcNow; }
        }
        if (ts.Kind != DateTimeKind.Utc) ts = ts.ToUniversalTime();

        var sev = status.Equals("fail", StringComparison.OrdinalIgnoreCase)
            ? ProjectedEventSeverity.Error
            : status.Equals("warn", StringComparison.OrdinalIgnoreCase) || status.Equals("partial", StringComparison.OrdinalIgnoreCase)
                ? ProjectedEventSeverity.Warn
                : ProjectedEventSeverity.Info;

        return new RawSourceEvent
        {
            Id = $"aspect:{aspect}",
            Kind = "workbench.aspectVerdict",
            SourceKind = "auto-review",
            Role = "orchestrator",
            TimestampUtc = ts,
            BodyMarkdown = fm.Body,
            Summary = summary,
            Severity = sev,
            Refs = new[] { $"aspect:{aspect}" },
            Metadata = new Dictionary<string, object?>
            {
                ["aspect"] = aspect,
                ["status"] = status
            }
        };
    }
}
