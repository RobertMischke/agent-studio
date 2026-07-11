using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

public enum WorkstreamCollectorVerdict { Skipped, Updated, Error }

public sealed record WorkstreamCollectorResult(
    WorkstreamCollectorVerdict Verdict,
    string Reason,
    int Writes = 0,
    int Rejected = 0,
    string? Model = null);

public sealed record WorkstreamCollectorContext
{
    public TaskInfo Task { get; init; } = null!;
    public WatchPathEntry Project { get; init; } = null!;
    public string TaskBody { get; init; } = "";
    public string StatusSummary { get; init; } = "";
    public string DiffSummary { get; init; } = "";
    public string ReviewSummary { get; init; } = "";
    public string Model { get; init; } = "";
    public string Cli { get; init; } = CliTypes.Claude;
    public string? ThinkingLevel { get; init; }
    public EngineeringWorkstreamFrameLanguage FrameLanguage { get; init; } = EngineeringWorkstreamFrameLanguage.English;
}

public sealed record WorkstreamCollectorProposal
{
    public List<WorkstreamCollectorItem> Items { get; init; } = [];
}

public sealed record WorkstreamCollectorItem
{
    public string Area { get; init; } = "";
    public string Identity { get; init; } = "";
    public string Title { get; init; } = "";
    public string Content { get; init; } = "";
    public string? Frequency { get; init; }
    public string? LastUpdatedFrom { get; init; }
    public string? Status { get; init; }
    public string? HumanAction { get; init; }
}

/// <summary>
/// EW-2 completion collector. A model classifies settled task evidence into the
/// fixed Workstream frame, while this class owns every write and enforces the
/// anti-overgrowth contract. Model output is data, never a filesystem command.
/// </summary>
public sealed class WorkstreamCollectorPostStepRunner
{
    public const string TemplateName = "workstream-collector.md";
    public const int MaxItemsPerRun = 8;
    public const int MaxItemsPerAreaPerRun = 3;
    public const int MaxPagesPerArea = 40;
    public const int MaxLogEntries = 100;
    public const int MaxRelativeDepth = 2;
    public const int MaxContentChars = 4000;

    private static readonly Regex JsonBlock = new(
        @"<!-- WORKSTREAM_COLLECTOR_JSON -->\s*```json\s*(?<json>\{.*?\})\s*```",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly RuntimePromptService _prompts;
    private readonly ILogger<WorkstreamCollectorPostStepRunner> _logger;
    private readonly CliOneShotRegistry? _oneShots;

    public Func<CliOneShotRequest, CancellationToken, Task<CliOneShotResult>>? OneShotOverride { get; set; }

    public WorkstreamCollectorPostStepRunner(
        RuntimePromptService prompts,
        ILogger<WorkstreamCollectorPostStepRunner> logger,
        CliOneShotRegistry? oneShots = null)
    {
        _prompts = prompts;
        _logger = logger;
        _oneShots = oneShots;
    }

    public async Task<WorkstreamCollectorResult> RunAsync(WorkstreamCollectorContext ctx, CancellationToken ct)
    {
        if (ctx.Task == null || string.IsNullOrWhiteSpace(ctx.Project.RootPath))
            return new(WorkstreamCollectorVerdict.Skipped, "project root is not configured", Model: ctx.Model);

        var docsRoot = Path.Combine(ctx.Project.RootPath, "docs");
        EngineeringWorkstreamFrameSeeder.EnsureFrame(docsRoot, ctx.FrameLanguage);
        var prompt = BuildPrompt(ctx, docsRoot);
        var request = new CliOneShotRequest(ctx.Cli, ctx.Model, prompt)
        {
            ThinkingLevel = ctx.ThinkingLevel,
            Timeout = TimeSpan.FromSeconds(180),
            Source = AdHocUsageSources.WorkstreamCollector,
            Project = ctx.Project.Name,
            JobId = ctx.Task.Id,
            RecordUsage = true,
            JobFolderPath = ctx.Task.FolderPath,
            StepId = PipelineCatalogue.WorkstreamCollectorStepId,
            TemplateRef = TemplateName,
        };

        try
        {
            var result = OneShotOverride != null
                ? await OneShotOverride(request, ct)
                : await RunOneShotAsync(request, ct);
            if (!result.Ok)
                return new(WorkstreamCollectorVerdict.Error, result.Error ?? "collector CLI failed", Model: ctx.Model);

            var reply = string.IsNullOrWhiteSpace(result.ParsedText) ? result.Stdout : result.ParsedText;
            var proposal = Parse(reply);
            if (proposal == null)
                return new(WorkstreamCollectorVerdict.Error, "collector response did not contain valid JSON", Model: ctx.Model);

            // Workstream Log is the one mandatory area. A malformed model reply
            // must not silently create a gap in the chronological record.
            if (!proposal.Items.Any(i =>
                string.Equals(i.Area, "50-workstream-log", StringComparison.OrdinalIgnoreCase)
                || string.Equals(i.Area, "Workstream Log", StringComparison.OrdinalIgnoreCase)))
            {
                proposal.Items.Insert(0, new WorkstreamCollectorItem
                {
                    Area = "50-workstream-log",
                    Identity = "task-outcome",
                    Title = string.IsNullOrWhiteSpace(ctx.Task.Title)
                        ? (ctx.Task.Key ?? ctx.Task.Id)
                        : ctx.Task.Title!,
                    Content = $"Task {ctx.Task.Key ?? ctx.Task.Id} completed; no model-authored log summary was returned.",
                });
            }

            var (writes, rejected) = Apply(docsRoot, ctx, proposal, DateTime.UtcNow);
            _logger.LogInformation(
                "workstream_collector_completed project={Project} job={JobId} writes={Writes} rejected={Rejected} model={Model}",
                ctx.Project.Name, ctx.Task.Id, writes, rejected, ctx.Model);
            return new(WorkstreamCollectorVerdict.Updated,
                $"applied {writes} workstream updates; rejected {rejected}", writes, rejected, ctx.Model);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "workstream_collector_failed project={Project} job={JobId}", ctx.Project.Name, ctx.Task.Id);
            return new(WorkstreamCollectorVerdict.Error, ex.Message, Model: ctx.Model);
        }
    }

    /// <summary>
    /// Deterministic onboarding half of EW-2. It publishes the task as the
    /// current development state before CORE starts, using the same bounded
    /// writer as completion. The later model pass can replace it when settled.
    /// </summary>
    public static (int Writes, int Rejected) RecordOnboarding(
        string projectRoot, TaskInfo task, EngineeringWorkstreamFrameLanguage language, DateTime? now = null)
    {
        var docsRoot = Path.Combine(projectRoot, "docs");
        EngineeringWorkstreamFrameSeeder.EnsureFrame(docsRoot, language);
        var key = task.Key ?? task.Id;
        var proposal = new WorkstreamCollectorProposal
        {
            Items =
            [
                new WorkstreamCollectorItem
                {
                    Area = "10-current-development-state",
                    Identity = "current",
                    Title = string.IsNullOrWhiteSpace(task.Title) ? key : task.Title!,
                    Content = $"Active task: **{key}**. Status: task execution started.",
                },
            ],
        };
        return Apply(docsRoot, new WorkstreamCollectorContext
        {
            Task = task,
            Project = new WatchPathEntry { RootPath = projectRoot, Path = projectRoot, Name = task.ProjectName },
            FrameLanguage = language,
        }, proposal, now ?? DateTime.UtcNow);
    }

    internal string BuildPrompt(WorkstreamCollectorContext ctx, string docsRoot)
    {
        var values = new Dictionary<string, string?>
        {
            ["frame_map"] = RenderFrameMap(),
            ["known_pages"] = RenderKnownPages(docsRoot),
            ["task_key"] = ctx.Task.Key ?? ctx.Task.Id,
            ["task_title"] = ctx.Task.Title ?? ctx.Task.Id,
            ["task_body"] = Trim(ctx.TaskBody, 6000),
            ["status_summary"] = Trim(ctx.StatusSummary, 4000),
            ["diff_summary"] = Trim(ctx.DiffSummary, 8000),
            ["review_summary"] = Trim(ctx.ReviewSummary, 4000),
            ["budgets"] = $"max {MaxItemsPerRun} items/run, max {MaxItemsPerAreaPerRun} items/area/run, max {MaxPagesPerArea} pages/area, max depth {MaxRelativeDepth}, max {MaxContentChars} chars/item, max {MaxLogEntries} log entries",
        };
        return _prompts.Render(TemplateName, values);
    }

    internal static WorkstreamCollectorProposal? Parse(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var match = JsonBlock.Match(reply);
        if (!match.Success) return null;
        try
        {
            return JsonSerializer.Deserialize<WorkstreamCollectorProposal>(match.Groups["json"].Value,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException) { return null; }
    }

    internal static (int Writes, int Rejected) Apply(
        string docsRoot, WorkstreamCollectorContext ctx, WorkstreamCollectorProposal proposal, DateTime now)
    {
        var accepted = proposal.Items.Take(MaxItemsPerRun).ToList();
        var rejected = Math.Max(0, proposal.Items.Count - accepted.Count);
        var writes = 0;
        var perArea = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in accepted)
        {
            if (!TryArea(item.Area, out var area) || !ValidIdentity(item.Identity)
                || string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Content))
            { rejected++; continue; }
            var count = perArea.GetValueOrDefault(area.Slug);
            var areaBudget = area.Slug == "50-workstream-log" ? 1 : MaxItemsPerAreaPerRun;
            if (count >= areaBudget) { rejected++; continue; }
            perArea[area.Slug] = count + 1;

            var areaRoot = Path.Combine(docsRoot, Native(area.FolderRel));
            Directory.CreateDirectory(areaRoot);
            var identity = NormalizeIdentity(item.Identity);
            var taskRef = ctx.Task.Key ?? ctx.Task.Id;
            if (area.Slug == "50-workstream-log")
            {
                AppendLog(Path.Combine(areaRoot, "workstream-log.md"), item, taskRef, now);
                writes++;
                continue;
            }

            if (Directory.EnumerateFiles(areaRoot, "*.md", SearchOption.AllDirectories).Count() >= MaxPagesPerArea
                && !File.Exists(Path.Combine(areaRoot, Native(identity + ".md"))))
            { rejected++; continue; }

            var path = area.Slug == "10-current-development-state"
                ? Path.Combine(areaRoot, "current.md")
                : Path.Combine(areaRoot, Native(identity + ".md"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var frequency = area.Slug == "20-development-signals"
                ? NextFrequency(path, item.Frequency, taskRef)
                : (int?)null;
            var lastUpdatedFrom = area.Slug == "30-system-knowledge"
                ? (string.IsNullOrWhiteSpace(item.LastUpdatedFrom) ? taskRef : item.LastUpdatedFrom!.Trim())
                : null;
            File.WriteAllText(path, RenderPage(item, identity, taskRef, frequency, lastUpdatedFrom, now), new UTF8Encoding(false));
            writes++;
        }
        return (writes, rejected);
    }

    private async Task<CliOneShotResult> RunOneShotAsync(CliOneShotRequest request, CancellationToken ct)
    {
        var oneShot = _oneShots?.Get(request.CliType) ?? _oneShots?.Get(CliTypes.Claude);
        if (oneShot != null) return await oneShot.RunAsync(request, ct);
        var now = DateTime.UtcNow;
        return CliOneShotResult.SpawnFailure("no one-shot CLI registered", now, now);
    }

    private static string RenderFrameMap() => string.Join("\n", EngineeringWorkstreamFrame.Areas.Select(a =>
        $"- {a.Slug}: {a.Title}. {a.Purpose}"));

    private static string RenderKnownPages(string docsRoot)
    {
        var root = Path.Combine(docsRoot, Native(EngineeringWorkstreamFrame.FrameRootRel));
        if (!Directory.Exists(root)) return "(none)";
        var pages = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(docsRoot, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(256).ToList();
        return pages.Count == 0 ? "(none)" : string.Join("\n", pages.Select(p => "- " + p));
    }

    private static bool TryArea(string value, out EngineeringWorkstreamFrame.FrameArea area)
    {
        area = EngineeringWorkstreamFrame.Areas.FirstOrDefault(a =>
            a.Slug.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase)
            || a.Title.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase))!;
        return area != null;
    }

    private static bool ValidIdentity(string? value)
    {
        var normalized = NormalizeIdentity(value ?? "");
        if (normalized.Length == 0 || normalized.Contains("..", StringComparison.Ordinal)) return false;
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= MaxRelativeDepth && parts.All(p => Regex.IsMatch(p, "^[a-z0-9][a-z0-9-]*$"));
    }

    private static string NormalizeIdentity(string value) => value.Trim().Replace('\\', '/').Trim('/').ToLowerInvariant();
    private static string Native(string value) => value.Replace('/', Path.DirectorySeparatorChar);

    private static int NextFrequency(string path, string? proposed, string taskRef)
    {
        var old = 0;
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            var m = Regex.Match(existing, @"(?m)^frequency:\s*(\d+)\s*$");
            if (m.Success) int.TryParse(m.Groups[1].Value, out old);
            if (Regex.IsMatch(existing,
                @"(?m)^updated-from:\s*" + Regex.Escape(taskRef) + @"\s*$",
                RegexOptions.IgnoreCase))
                return Math.Max(1, old);
        }
        var supplied = int.TryParse(proposed, out var n) ? Math.Max(1, n) : 1;
        return old + supplied;
    }

    private static string RenderPage(WorkstreamCollectorItem item, string identity, string taskRef,
        int? frequency, string? lastUpdatedFrom, DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("identity: " + identity);
        sb.AppendLine("title: \"" + item.Title.Trim().Replace("\"", "\\\"") + "\"");
        sb.AppendLine("updated: " + now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        sb.AppendLine("updated-from: " + taskRef);
        if (frequency.HasValue) sb.AppendLine("frequency: " + frequency.Value.ToString(CultureInfo.InvariantCulture));
        if (string.Equals(item.Area, "20-development-signals", StringComparison.OrdinalIgnoreCase))
        {
            var status = item.Status?.Trim().ToLowerInvariant();
            if (status is "observed" or "active" or "resolved") sb.AppendLine("status: " + status);
            if (!string.IsNullOrWhiteSpace(item.HumanAction))
                sb.AppendLine("human-action: \"" + item.HumanAction.Trim().Replace("\"", "\\\"") + "\"");
        }
        if (lastUpdatedFrom != null) sb.AppendLine("last-updated-from: " + lastUpdatedFrom);
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("# " + item.Title.Trim());
        sb.AppendLine();
        if (lastUpdatedFrom != null) sb.AppendLine("**Last Updated From:** " + lastUpdatedFrom + "\n");
        sb.AppendLine(Trim(item.Content, MaxContentChars));
        return sb.ToString();
    }

    private static void AppendLog(string path, WorkstreamCollectorItem item, string taskRef, DateTime now)
    {
        var entries = File.Exists(path)
            ? Regex.Split(File.ReadAllText(path), @"(?m)(?=^## )").Where(s => s.StartsWith("## ")).ToList()
            : [];
        entries.RemoveAll(e => Regex.IsMatch(e,
            @"(?m)^Source:\s*" + Regex.Escape(taskRef) + @"\s*$",
            RegexOptions.IgnoreCase));
        var entry = $"## {now:yyyy-MM-dd} - {item.Title.Trim()}\n\nSource: {taskRef}\n\n{Trim(item.Content, MaxContentChars)}\n";
        entries.Insert(0, entry);
        entries = entries.Take(MaxLogEntries).ToList();
        File.WriteAllText(path,
            "# Workstream Log\n\nNewest entry first. Entries are bounded by the EW-2 collector budget.\n\n" +
            string.Join("\n", entries), new UTF8Encoding(false));
    }

    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var clean = value.Trim();
        return clean.Length <= max ? clean : clean[..max].TrimEnd() + "...";
    }
}
