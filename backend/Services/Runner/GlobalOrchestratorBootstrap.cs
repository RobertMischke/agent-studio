using OrchestratorApi.Models;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Tasks;

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
    private readonly TaskScannerService _scanner;
    private readonly IConfiguration _config;
    private readonly ClientIdentityStore? _identityStore;

    public GlobalOrchestratorBootstrap(
        ILogger<GlobalOrchestratorBootstrap> logger,
        GlobalOrchestratorSessionStore store,
        OrchestratorRunner runner,
        TaskScannerService scanner,
        IConfiguration config,
        ClientIdentityStore? identityStore = null)
    {
        _logger = logger;
        _store = store;
        _runner = runner;
        _scanner = scanner;
        _config = config;
        _identityStore = identityStore;
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

    /// <summary>
    /// Render the boot prompt used to seed the singleton global session.
    /// Exposed so <see cref="OrchestratorChatService"/> can reuse the same
    /// framing when re-bootstrapping after a rejected resume (see
    /// <see cref="OrchestratorRunner.ResumeWithFallbackAsync"/>). The
    /// rendered text is snapshot of watched projects + a brief role
    /// instruction; safe to recompute on demand.
    /// </summary>
    public string BuildBootPrompt()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are the GLOBAL orchestrator for Agent Software Studio.");
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
            if (IsSelfModificationTarget(e))
            {
                sb.AppendLine("    NOTE: this project is the tool itself - any change here affects your own runtime.");
                sb.AppendLine("          Prefer splitting risky changes into smaller tasks; flag self-mod scope explicitly.");
            }
        }
        sb.AppendLine();

        // User preferences (bootstrap-time defaults). The per-turn prompt
        // re-emits a fresher block based on the chatting client's identity,
        // so this acts as the "no client context yet" fallback.
        AppendBootUserPreferences(sb);

        // Inline tool inventory so the orchestrator does not assume it
        // lacks API access. Without this, a "create me three tasks" request
        // gets refused with "you have to do it yourself in the UI" because
        // the model has no representation of the local HTTP surface.
        AppendToolInventory(sb);

        // Light project-state snapshot so the orchestrator is grounded in
        // current reality rather than a one-time enumeration. Cap to a few
        // jobs per state to keep the boot cheap.
        try
        {
            var jobs = _scanner.ScanAllJobs();
            sb.AppendLine($"Current tasks across all projects ({jobs.Count} total):");
            sb.AppendLine("(These items are called \"tasks\" in user-facing vocabulary; never use \"jobs\".)");
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

    /// <summary>
    /// Resolve the "boot-time" user defaults from the bootstrap identity
    /// (<see cref="DefaultClientIdentity.Id"/>). Falls back to the historic
    /// default CLI plus the orchestrator runner's default model so a fresh
    /// install with no identity record still produces a well-formed block.
    /// </summary>
    internal static (string cli, string model) ResolveBootDefaults(ClientIdentityStore? identityStore)
    {
        var rec = identityStore?.Find(DefaultClientIdentity.Id);
        var cli = !string.IsNullOrWhiteSpace(rec?.DefaultCliType) ? rec!.DefaultCliType! : "claude";
        var model = !string.IsNullOrWhiteSpace(rec?.DefaultModel) ? rec!.DefaultModel! : OrchestratorRunner.DefaultModel;
        return (cli, model);
    }

    private void AppendBootUserPreferences(System.Text.StringBuilder sb)
    {
        var (cli, model) = ResolveBootDefaults(_identityStore);
        AppendUserPreferencesBlock(sb, header: "=== USER PREFERENCES ===", cli, model);
    }

    /// <summary>
    /// Shared renderer for the user-preferences block. Used at boot with the
    /// bootstrap identity, and re-emitted on every chat turn with the live
    /// values for the active client. Kept in one place so the two emissions
    /// can't drift on wording.
    /// </summary>
    internal static void AppendUserPreferencesBlock(System.Text.StringBuilder sb, string header, string cli, string model)
    {
        sb.AppendLine(header);
        sb.AppendLine($"Default CLI: {cli}");
        sb.AppendLine($"Default model: {model}");
        sb.AppendLine("If the user asks you to create a task without naming a CLI or model, use these defaults.");
        sb.AppendLine("Do not invent other models; if the user wants a different one they will say so.");
        sb.AppendLine();
    }

    private static void AppendToolInventory(System.Text.StringBuilder sb)
    {
        sb.AppendLine("=== AVAILABLE TOOLS ===");
        sb.AppendLine("You have:");
        sb.AppendLine("- Read, Edit, Write, Bash, Glob, Grep (standard Claude tools).");
        sb.AppendLine("- HTTP via Bash: you can POST/PUT/GET against http://127.0.0.1:5030/api/* with header X-Client-Id: <the user's id> (the user's identity is forwarded).");
        sb.AppendLine("- To create a task: POST /api/tasks with JSON body { id, title, watchPath, agent, cliType, model, targetState, promptMarkdown }. Pick cliType/model from the USER PREFERENCES block above unless the user names a different one.");
        sb.AppendLine("- To move a task between lanes: POST /api/tasks/{id}/move?watchPath=... with { targetState }.");
        sb.AppendLine("- To set a task's model: PUT /api/tasks/{id}/model?watchPath=... with { model }.");
        sb.AppendLine("- To change a runner's mode: PUT /api/runner/{projectName}/mode with { mode: \"auto-continuous\" | \"auto-single\" | \"manual\" | \"paused\" }.");
        sb.AppendLine();
        sb.AppendLine("If the user asks you to create N tasks, do it yourself via the API (one POST per task) and report what you did.");
        sb.AppendLine("Do NOT tell them they have to do it manually in the UI - that is wrong, you have the API.");
        sb.AppendLine();
    }

    /// <summary>
    /// Heuristic: is the watched entry the agent-orchestrator codebase
    /// itself? Matched on either the working directory name or the task
    /// folder name. Kept case-insensitive and tolerant to either checkout
    /// flavour (dev vs stable) so future renames don't silently disable
    /// the warning.
    /// </summary>
    internal static bool IsSelfModificationTarget(WatchPathEntry e)
    {
        bool Matches(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return false;
            var leaf = System.IO.Path.GetFileName(p.TrimEnd('/', '\\'));
            if (string.IsNullOrWhiteSpace(leaf)) return false;
            return leaf.StartsWith("agent-taskboard", StringComparison.OrdinalIgnoreCase)
                || leaf.StartsWith("agent-orchestrator", StringComparison.OrdinalIgnoreCase);
        }
        return Matches(e.RootPath) || Matches(e.RepositoryPath) || Matches(e.Path) || Matches(e.Name);
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
