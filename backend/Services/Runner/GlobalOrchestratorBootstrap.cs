using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Boots the singleton <see cref="GlobalOrchestratorSession"/> at app
/// start, reusing the persisted id when one exists. Mirrors the per-
/// project boot flow in <see cref="ProjectRunner.BootOrchestratorSessionAsync"/>
/// so the failure modes and chat output stay consistent; the only
/// differences are scope (one session for the whole app, not per
/// watched project) and the boot prompt (a roll-up of all watched
/// projects, not the contents of one project's docs).
/// </summary>
public sealed class GlobalOrchestratorBootstrap
{
    private readonly ILogger<GlobalOrchestratorBootstrap> _logger;
    private readonly GlobalOrchestratorSessionStore _store;
    private readonly OrchestratorRunner _runner;
    private readonly JobScannerService _scanner;
    private readonly IConfiguration _config;

    public GlobalOrchestratorBootstrap(
        ILogger<GlobalOrchestratorBootstrap> logger,
        GlobalOrchestratorSessionStore store,
        OrchestratorRunner runner,
        JobScannerService scanner,
        IConfiguration config)
    {
        _logger = logger;
        _store = store;
        _runner = runner;
        _scanner = scanner;
        _config = config;
    }

    public async Task BootAsync(CancellationToken ct)
    {
        var existing = _store.Read();
        if (existing != null && !string.IsNullOrWhiteSpace(existing.SessionId))
        {
            _logger.LogInformation(
                "[global-orchestrator] reusing persisted session {SessionId} (calls so far: {Calls})",
                existing.SessionId, existing.Calls);
            return;
        }

        var modelId = _config["GlobalOrchestrator:Model"] ?? OrchestratorRunner.DefaultModel;
        var prompt = BuildBootPrompt();
        var workingDirectory = ResolveWorkingDirectory();

        _logger.LogInformation("[global-orchestrator] booting on {Model} at {Cwd}", modelId, workingDirectory);
        var result = await _runner.DecideAsync(prompt, modelId, workingDirectory, ct);

        if (!result.Success || string.IsNullOrWhiteSpace(result.CapturedSessionId))
        {
            _logger.LogWarning(
                "[global-orchestrator] boot failed: success={Success}, sessionId={SessionId}, error={Error}",
                result.Success, result.CapturedSessionId, result.ErrorMessage);
            return;
        }

        var session = new GlobalOrchestratorSession(
            SessionId: result.CapturedSessionId!,
            Model: result.Model,
            BootedAt: DateTime.UtcNow,
            BootPromptPreview: TruncatePreview(prompt, 2000),
            BootReplyPreview: TruncatePreview(result.ReplyText, 600),
            CumulativeInputTokens: result.TokenUsage?.InputTokens ?? 0,
            CumulativeOutputTokens: result.TokenUsage?.OutputTokens ?? 0,
            CumulativeCacheReadTokens: result.TokenUsage?.CacheReadTokens ?? 0,
            CumulativeCacheCreationTokens: result.TokenUsage?.CacheCreationTokens ?? 0,
            Calls: 1,
            LastUsedAt: DateTime.UtcNow,
            LastError: null);
        _store.Write(session);
    }

    private string BuildBootPrompt()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are the GLOBAL orchestrator for Agent Task Processor.");
        sb.AppendLine();
        sb.AppendLine("Scope. There is one of you for the whole app, sitting above the per-project orchestrators.");
        sb.AppendLine("Per-project orchestrators answer single-task questions on behalf of the user when an");
        sb.AppendLine("agent emits NEEDS_INPUT in auto mode. Your role is cross-project: priorities, idle vs.");
        sb.AppendLine("starving projects, suggesting which project to look at first, summarising what is");
        sb.AppendLine("happening across the board.");
        sb.AppendLine();

        var entries = _scanner.GetWatchPaths();
        sb.AppendLine($"Watched projects ({entries.Count}):");
        foreach (var e in entries)
        {
            sb.AppendLine($"- {e.Name}");
            if (!string.IsNullOrWhiteSpace(e.RootPath)) sb.AppendLine($"    working directory: {e.RootPath}");
            if (!string.IsNullOrWhiteSpace(e.RepositoryPath)) sb.AppendLine($"    git repository:    {e.RepositoryPath}");
            sb.AppendLine($"    task folder:       {e.Path}");
        }
        sb.AppendLine();

        // Light project-state snapshot so the orchestrator is grounded in
        // current reality rather than a one-time enumeration. Cap to a few
        // jobs per state to keep the boot cheap.
        try
        {
            var jobs = _scanner.ScanAllJobs();
            sb.AppendLine($"Current jobs across all projects ({jobs.Count} total):");
            foreach (var grp in jobs.GroupBy(j => j.ProjectName))
            {
                sb.AppendLine($"  {grp.Key}:");
                foreach (var sg in grp.GroupBy(j => j.State).OrderBy(g => g.Key))
                    sb.AppendLine($"    {sg.Key}: {sg.Count()}");
            }
            sb.AppendLine();
        }
        catch { /* boot is best-effort; missing snapshot is fine */ }

        sb.AppendLine("Your job:");
        sb.AppendLine("- When asked which project needs attention, weigh queue depth and last activity.");
        sb.AppendLine("- When asked for a board summary, keep it short and concrete (a few sentences).");
        sb.AppendLine("- Defer to the per-project orchestrator on per-task decisions; you should not");
        sb.AppendLine("  reach into a single task's NEEDS_INPUT - that is the per-project orchestrator's role.");
        sb.AppendLine("- If a question requires user knowledge you do not have, reply with exactly: BLOCK");
        sb.AppendLine();
        sb.AppendLine("Acknowledge readiness with one short sentence naming how many projects you saw.");
        return sb.ToString();
    }

    private string ResolveWorkingDirectory()
    {
        // Use the first watched project's root as the working directory so
        // claude resolves relative paths somewhere stable. Falls back to
        // the temp directory if no projects are configured.
        var first = _scanner.GetWatchPaths().FirstOrDefault();
        return !string.IsNullOrWhiteSpace(first?.RootPath)
            ? first!.RootPath
            : Path.GetTempPath();
    }

    private static string TruncatePreview(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "...";
    }
}
