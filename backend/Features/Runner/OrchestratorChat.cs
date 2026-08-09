using System.Text;
using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.Orchestrator;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Tasks;

namespace AgentStudio.Runner;

/// <summary>
/// Per-project conversation log between the user and the (global) orchestrator
/// session. Lives next to the per-project orchestrator log as
/// <c>&lt;watchPath&gt;/.orchestrator/orchestrator-chat.jsonl</c>: one JSONL
/// turn per line, oldest first, tolerant to torn writes.
///
/// <para>
/// This is the storage layer for Phase 3 of the side-sheet chat. Phase 2
/// re-used the existing override endpoint to steer the *agent* on an
/// already-running task; Phase 3 introduces a real bidirectional chat with
/// the *orchestrator itself*: the user asks "where do you stand?", the
/// orchestrator replies, the dialogue accumulates. Conversation memory
/// lives in the global Claude session (resumed via <c>-r &lt;sessionId&gt;</c>
/// on each turn), so the only on-disk state we need is the audit log of
/// what was said when.
/// </para>
/// </summary>
public class OrchestratorChat
{
    private readonly ILogger<OrchestratorChat> _logger;
    private readonly ProjectChatStore? _projectStore;
    private readonly ProjectChatIndex? _projectIndex;
    private readonly TaskScannerService? _scanner;

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OrchestratorChat(
        ILogger<OrchestratorChat> logger,
        ProjectChatStore? projectStore = null,
        ProjectChatIndex? projectIndex = null,
        TaskScannerService? scanner = null)
    {
        _logger = logger;
        _projectStore = projectStore;
        _projectIndex = projectIndex;
        _scanner = scanner;
    }

    public bool Append(string watchPath, OrchestratorChatTurn turn) => Append(watchPath, turn, context: null);

    /// <summary>
    /// Append one turn to the transcript for a specific navigation context
    /// (MC-2, Concept §4). A <see cref="OrchestratorContextKey.TaskKind"/>
    /// context is persisted to its own per-task file so a task page and the
    /// board no longer share one history; <c>project</c> / <c>global</c> /
    /// <c>null</c> resolve to the canonical per-project
    /// <c>orchestrator-chat.jsonl</c>, so existing project chats are
    /// unaffected. Only project-scoped turns mirror into the project chat
    /// tree; task threads stay out of the project-level FTS index.
    /// </summary>
    public bool Append(string watchPath, OrchestratorChatTurn turn, OrchestratorContextKey? context)
    {
        if (string.IsNullOrWhiteSpace(watchPath)) return false;
        try
        {
            var path = ResolveContextPath(watchPath, context);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(turn, WriteOpts) + Environment.NewLine;
            File.AppendAllText(path, line, Encoding.UTF8);

            // Slice D mirror: also write the per-turn markdown file so the
            // new file-tree + FTS index stay current as turns are appended.
            // Best-effort; legacy JSONL remains the fallback if this fails.
            // Only the project-scoped thread mirrors — the project chat tree
            // is per-project, so folding task-context turns into it would
            // cross-contaminate the board's history.
            if (!IsTaskContext(context))
                MirrorToProjectChat(watchPath, turn);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append orchestrator chat turn under {WatchPath}", watchPath);
            return false;
        }
    }

    private void MirrorToProjectChat(string watchPath, OrchestratorChatTurn turn)
    {
        if (_projectStore == null || _scanner == null) return;
        try
        {
            var entry = _scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Path, watchPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.RootPath, watchPath, StringComparison.OrdinalIgnoreCase));
            var projectFolder = entry?.Path;
            if (string.IsNullOrWhiteSpace(projectFolder)) return;

            var author = turn.Role switch
            {
                "user" => ProjectChatTurnAuthors.User,
                "orchestrator" => ProjectChatTurnAuthors.Orchestrator,
                _ => ProjectChatTurnAuthors.Orchestrator
            };
            var body = turn.Text ?? "";
            if (!string.IsNullOrWhiteSpace(turn.ErrorMessage))
            {
                body = string.IsNullOrEmpty(body)
                    ? "_error:_ " + turn.ErrorMessage
                    : body + "\n\n_error:_ " + turn.ErrorMessage;
            }

            var pTurn = new ProjectChatTurn
            {
                TurnId = turn.Id,
                Author = author,
                Kind = ProjectChatTurnKinds.Turn,
                Ts = DateTime.SpecifyKind(turn.Ts, DateTimeKind.Utc),
                Body = body
            };
            var written = _projectStore.Write(projectFolder, pTurn);
            _projectIndex?.Upsert(projectFolder, pTurn, written);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mirror to project chat tree failed for {WatchPath}", watchPath);
        }
    }

    public List<OrchestratorChatTurn> Read(string watchPath) => Read(watchPath, context: null);

    /// <summary>
    /// Read the transcript for a specific navigation context (MC-2). Returns
    /// the per-task thread for a task context and the canonical per-project
    /// thread otherwise. An absent file is an empty (not failed) transcript.
    /// </summary>
    public List<OrchestratorChatTurn> Read(string watchPath, OrchestratorContextKey? context)
    {
        var result = new List<OrchestratorChatTurn>();
        if (string.IsNullOrWhiteSpace(watchPath)) return result;
        var path = ResolveContextPath(watchPath, context);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var turn = JsonSerializer.Deserialize<OrchestratorChatTurn>(line, ReadOpts);
                if (turn != null) result.Add(turn);
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "OrchestratorChat: Best-effort: skip torn / malformed lines.");
                // Best-effort: skip torn / malformed lines.
            }
        }
        return result;
    }

    private static string ResolvePath(string watchPath) =>
        Path.Combine(watchPath, ".orchestrator", "orchestrator-chat.jsonl");

    private static bool IsTaskContext(OrchestratorContextKey? context) =>
        context != null && context.Kind == OrchestratorContextKey.TaskKind;

    /// <summary>
    /// Resolve the on-disk transcript file for a navigation context. Task
    /// contexts get a dedicated file under <c>.orchestrator/context-chats/</c>
    /// keyed by the reversible <see cref="OrchestratorContextKey.Encode"/>
    /// folder-safe form; every other context (including <c>null</c>) resolves
    /// to the legacy per-project <c>orchestrator-chat.jsonl</c> so the board
    /// thread and older callers are byte-for-byte unchanged.
    /// </summary>
    internal static string ResolveContextPath(string watchPath, OrchestratorContextKey? context)
    {
        if (!IsTaskContext(context))
            return ResolvePath(watchPath);
        return Path.Combine(watchPath, ".orchestrator", "context-chats", context!.Encode() + ".jsonl");
    }

}

/// <summary>
/// One turn in the per-project orchestrator chat. <see cref="Role"/> is
/// "user" for the human's messages and "orchestrator" for the model's
/// replies; <see cref="ErrorMessage"/> is set on a failed turn so the
/// frontend can surface what went wrong without losing the user's text.
/// </summary>
public record OrchestratorChatTurn
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime Ts { get; init; } = DateTime.UtcNow;
    public string Role { get; init; } = OrchestratorChatRoles.User;
    public string Text { get; init; } = "";
    public string? Model { get; init; }
    public OrchestratorTokenUsage? TokenUsage { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>
    /// Persisted transparency receipt for the context composed into this
    /// reply's request. Null on user turns and legacy replies.
    /// </summary>
    public OrchestratorContextReceipt? ContextReceipt { get; init; }

    /// <summary>
    /// Raw / technical error detail preserved alongside a friendly
    /// <see cref="ErrorMessage"/>. The chat bubble shows the friendly
    /// message; the detail is what a developer needs to bisect (the
    /// original .NET exception text or CLI stderr). Set whenever
    /// <see cref="ErrorMessage"/> was produced by
    /// <see cref="OrchestratorChatErrorTranslator"/>; null on success
    /// turns and on legacy entries written before the translator existed.
    /// </summary>
    public string? ErrorDetail { get; init; }

}

public sealed record OrchestratorContextReceipt(
    string Scope,
    string ContextKey,
    string? TaskKey,
    IReadOnlyList<string> IncludedBlocks,
    DateTime CapturedAt);

internal sealed record OrchestratorChatPromptComposition(
    string Prompt,
    OrchestratorContextReceipt ContextReceipt);

public static class OrchestratorChatRoles
{
    public const string User = "user";
    public const string Orchestrator = "orchestrator";
}

public sealed record SendOrchestratorChatRequest(
    string Text,
    ChatNavigationContext? NavigationContext = null,
    string? Model = null,
    string? ThinkingLevel = null,
    string? SelectionSource = null);

/// <summary>
/// Structured navigation context the frontend ships with every project-chat
/// POST. The chat agent reads this to interpret context-dependent questions
/// ("what is the current task?", "explain this") against the page the
/// operator is actually looking at. Every field is optional from the
/// agent's perspective: a missing or null <see cref="CurrentTaskId"/> means
/// the operator is not on a task page and the agent must not invent one.
///
/// <para>
/// Background: before this field existed the agent answered context
/// questions in vacuum and hallucinated freely (see the 2026-05-09
/// "Conversation, Foul Conversation" incident). Carrying the navigation
/// state into the prompt closes that loop deterministically.
/// </para>
/// </summary>
public sealed record ChatNavigationContext(
    string? CurrentPage = null,
    string? CurrentTaskId = null,
    string? CurrentTaskKey = null,
    string? CurrentTaskTitle = null,
    string? CurrentTaskState = null,
    string? CurrentLaneFilter = null,
    string? ViewportTimestamp = null,
    string? ObservedSurface = null,
    string? AffectedComponent = null,
    string? PageRef = null,
    string? PageTitle = null,
    string? PageType = null,
    string? PageExcerpt = null);

/// <summary>
/// Service that turns a user message into an orchestrator reply with the
/// operator-selected Codex model and reasoning level, then persists both
/// turns to the context-specific chat log.
///
/// <para>
/// This operating mode is GPT-only. Each request carries the effective model
/// selection from the live Codex catalogue. A non-GPT model is rejected and
/// the runner has no Claude fallback.
/// </para>
/// </summary>
public class OrchestratorChatService
{
    private readonly OrchestratorChat _chat;
    private readonly OrchestratorRunner _runner;
    private readonly GlobalOrchestratorBootstrap _bootstrap;
    private readonly TaskScannerService _scanner;
    private readonly ILogger<OrchestratorChatService> _logger;
    private readonly ClientIdentityStore? _identityStore;
    private readonly OrchestratorContextDigestService? _contextDigests;
    private readonly ComponentRoutingService? _componentRouting;
    private readonly ProjectSettingsService? _projectSettings;
    private readonly ProjectRegistry? _projects;
    private readonly RemoteChatWorkBroker? _remoteWork;
    private readonly GitService? _git;
    private readonly OrchestratorTaskPromptContextComposer? _taskPromptContext;

    /// <summary>
    /// Serializes concurrent <see cref="SendAsync"/> calls so multiple Codex
    /// one-shots do not contend for the same orchestrator working directory
    /// and transcript writes. The user still sees the pending turn while it
    /// waits.
    ///
    /// <para>
    /// This is a pragmatic correctness guard, not a parallelism win:
    /// requests across all projects serialize on this gate. Per-context
    /// concurrency is outside this footer-selection change.
    /// </para>
    /// </summary>
    private static readonly SemaphoreSlim SessionGate = new(1, 1);

    public OrchestratorChatService(
        OrchestratorChat chat,
        OrchestratorRunner runner,
        GlobalOrchestratorSessionStore sessionStore,
        GlobalOrchestratorBootstrap bootstrap,
        TaskScannerService scanner,
        IConfiguration config,
        ILogger<OrchestratorChatService> logger,
        ClientIdentityStore? identityStore = null,
        OrchestratorContextDigestService? contextDigests = null,
        ComponentRoutingService? componentRouting = null,
        ProjectSettingsService? projectSettings = null,
        ProjectRegistry? projects = null,
        RemoteChatWorkBroker? remoteWork = null,
        GitService? git = null,
        OrchestratorTaskPromptContextComposer? taskPromptContext = null)
    {
        _chat = chat;
        _runner = runner;
        _bootstrap = bootstrap;
        _scanner = scanner;
        _logger = logger;
        _identityStore = identityStore;
        _contextDigests = contextDigests;
        _componentRouting = componentRouting;
        _projectSettings = projectSettings;
        _projects = projects;
        _remoteWork = remoteWork;
        _git = git;
        _taskPromptContext = taskPromptContext;
    }

    public List<OrchestratorChatTurn> Read(string watchPath) => _chat.Read(watchPath);

    /// <summary>Read the transcript for a specific navigation context (MC-2).</summary>
    public List<OrchestratorChatTurn> Read(string watchPath, OrchestratorContextKey? context)
        => _chat.Read(watchPath, context);

    public Task<OrchestratorChatTurn> SendAsync(
        string projectName,
        string watchPath,
        SendOrchestratorChatRequest req,
        CancellationToken ct)
        => SendAsync(projectName, watchPath, req, clientId: null, context: null, ct);

    public Task<OrchestratorChatTurn> SendAsync(
        string projectName,
        string watchPath,
        SendOrchestratorChatRequest req,
        string? clientId,
        CancellationToken ct)
        => SendAsync(projectName, watchPath, req, clientId, context: null, ct);

    /// <summary>
    /// Send a user message and persist both turns to the transcript for the
    /// given navigation context (MC-2, Concept §4). <paramref name="context"/>
    /// selects both the on-disk thread and the ORCH-1 read digest injected into
    /// this turn. The resumed Claude session and usage accounting remain shared,
    /// while project/task scoping is enforced by the digest builder. Passing
    /// <c>null</c> keeps the legacy project transcript and resolves an equivalent
    /// <c>project:&lt;projectName&gt;</c> digest.
    /// </summary>
    public async Task<OrchestratorChatTurn> SendAsync(
        string projectName,
        string watchPath,
        SendOrchestratorChatRequest req,
        string? clientId,
        OrchestratorContextKey? context,
        CancellationToken ct)
    {
        // Append the user turn outside the gate so the audit log records
        // the inbound message even if the user cancels while queued.
        var userTurn = new OrchestratorChatTurn
        {
            Role = OrchestratorChatRoles.User,
            Text = req.Text
        };
        _chat.Append(watchPath, userTurn, context);

        // Serialize on the singleton-session gate. Two concurrent resumes
        // race on the session id, the on-disk usage record, and Claude's
        // own session memory; the gate is the simplest correctness fix
        // until per-conversation sessions land. The wait counter tells us
        // when chats are actually queueing in the wild.
        var queuedAt = DateTime.UtcNow;
        await SessionGate.WaitAsync(ct);
        var queueWaitMs = (DateTime.UtcNow - queuedAt).TotalMilliseconds;
        if (queueWaitMs > 250)
        {
            _logger.LogInformation(
                "[orchestrator-chat] queued {WaitMs:F0}ms behind another in-flight chat for project {Project}",
                queueWaitMs, projectName);
        }
        try
        {
            var promptComposition = await BuildPromptAsync(projectName, watchPath, req, clientId, context, ct).ConfigureAwait(false);
            var prompt = promptComposition.Prompt;
            var contextReceipt = promptComposition.ContextReceipt;
            var requestedModel = string.IsNullOrWhiteSpace(req.Model)
                ? ModelMetadataRegistry.DefaultForCli(CliTypes.Codex) ?? ModelIds.Gpt55
                : req.Model.Trim();
            if (!requestedModel.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Orchestrator composer accepts GPT models only.");
            var thinkingLevel = string.IsNullOrWhiteSpace(req.ThinkingLevel)
                ? ModelMetadataRegistry.DefaultThinkingLevelForCli(CliTypes.Codex, requestedModel)
                : req.ThinkingLevel.Trim();
            var workingDirectory = ResolveWorkingDirectory(projectName, watchPath);
            OrchestratorDecisionResult result;
            try
            {
                var fullPrompt = _bootstrap.BuildBootPrompt() + "\n\n" + prompt;
                var remoteRoute = ResolveRemoteRoute(projectName, watchPath);
                if (remoteRoute != null && _remoteWork != null)
                {
                    var remote = await _remoteWork.EnqueueTurnAsync(
                        remoteRoute, fullPrompt, requestedModel, thinkingLevel, ct).ConfigureAwait(false);
                    result = new OrchestratorDecisionResult(
                        remote.Success,
                        remote.ReplyText,
                        string.IsNullOrWhiteSpace(remote.Model) ? requestedModel : remote.Model,
                        remote.TokenUsage,
                        CapturedSessionId: null,
                        remote.ErrorMessage);
                }
                else
                {
                    result = await _runner.DecideCodexAsync(
                        fullPrompt,
                        requestedModel,
                        thinkingLevel,
                        workingDirectory,
                        ct);
                }
            }
            catch (Exception ex)
            {
                // Full stacktrace at error level so the backend log is
                // self-sufficient for bisecting the 2026-05-24 pipe-closed
                // incident class. The chat bubble itself shows the friendly
                // translation produced below; the raw .NET message lands in
                // ErrorDetail (audit log + future UI expander) but never as
                // the primary bubble text.
                _logger.LogError(
                    ex,
                    "Orchestrator chat send threw for project {Project} ({ExceptionType}): {Raw}",
                    projectName, ex.GetType().Name, ex.Message);
                var translation = OrchestratorChatErrorTranslator.Translate(ex.Message, CliTypes.Codex);
                var failure = new OrchestratorChatTurn
                {
                    Role = OrchestratorChatRoles.Orchestrator,
                    Text = "",
                    ErrorMessage = translation.FriendlyMessage,
                    ErrorDetail = translation.RawDetail,
                    ContextReceipt = contextReceipt
                };
                _chat.Append(watchPath, failure, context);
                return failure;
            }

            if (!result.Success)
            {
                _logger.LogError(
                    "Orchestrator chat call failed for project {Project} (model={Model}): {Raw}",
                    projectName, result.Model, result.ErrorMessage ?? "(no error message)");
                var translation = OrchestratorChatErrorTranslator.Translate(result.ErrorMessage, CliTypes.Codex);
                var failure = new OrchestratorChatTurn
                {
                    Role = OrchestratorChatRoles.Orchestrator,
                    Text = result.ReplyText ?? "",
                    Model = result.Model,
                    TokenUsage = result.TokenUsage,
                    ErrorMessage = translation.FriendlyMessage,
                    ErrorDetail = translation.RawDetail,
                    ContextReceipt = contextReceipt
                };
                _chat.Append(watchPath, failure, context);
                return failure;
            }

            var reply = new OrchestratorChatTurn
            {
                Role = OrchestratorChatRoles.Orchestrator,
                Text = result.ReplyText,
                Model = result.Model,
                TokenUsage = result.TokenUsage,
                ContextReceipt = contextReceipt
            };
            _chat.Append(watchPath, reply, context);
            return reply;
        }
        finally
        {
            SessionGate.Release();
        }
    }

    private async Task<OrchestratorChatPromptComposition> BuildPromptAsync(
        string projectName,
        string watchPath,
        SendOrchestratorChatRequest req,
        string? clientId,
        OrchestratorContextKey? context,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        var includedBlocks = new List<string> { "active project" };
        sb.AppendLine("=== ACTIVE PROJECT CONTEXT ===");
        sb.AppendLine($"The user is currently looking at project: \"{projectName}\"");
        sb.AppendLine("This may be a different project than the one discussed earlier in this session.");
        sb.AppendLine("Answer ONLY about \"" + projectName + "\". Do not refer to other projects unless the user asks.");
        sb.AppendLine();

        var digestAdded = false;
        if (_contextDigests != null)
        {
            try
            {
                var effectiveContext = context;
                if (effectiveContext == null)
                    OrchestratorContextKey.TryParse($"project:{projectName}", out effectiveContext);
                if (effectiveContext != null)
                {
                    var digest = await _contextDigests.BuildAsync(effectiveContext, ct: ct).ConfigureAwait(false);
                    sb.AppendLine(digest.Digest);
                    sb.AppendLine();
                    digestAdded = true;
                    includedBlocks.Add("context digest");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                includedBlocks.Add("context digest: unavailable");
                _logger.LogWarning(
                    ex,
                    "orchestrator_context_digest_injection_failed contextKey={ContextKey} project={Project} fallback=project-state-snapshot",
                    context?.Value ?? $"project:{projectName}",
                    projectName);
            }
        }

        if (!digestAdded)
        {
            try
            {
                var tasks = _scanner.ScanAllAutomationJobs()
                    .Where(j => string.Equals(j.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                AppendProjectStateSnapshot(sb, projectName, tasks);
                includedBlocks.Add("project state snapshot");
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "OrchestratorChat: Best-effort: missing snapshot is fine; the orchestrator can");
                // Best-effort: missing snapshot is fine; the orchestrator can
                // still answer general questions from session memory.
            }
        }

        // Per-turn refresh of the user's CLI / model defaults. The boot
        // prompt embeds a stale snapshot, but the user can flip the default
        // from the UI at any time; without this block the orchestrator
        // would keep proposing the boot-time default forever. The block
        // also names the X-Client-Id the orchestrator should forward when
        // hitting /api/tasks on the user's behalf.
        AppendCurrentUserPreferences(sb, clientId, _identityStore);
        includedBlocks.Add("current user preferences");

        AppendNavigationContext(sb, req.NavigationContext);
        includedBlocks.Add(req.NavigationContext == null ? "navigation context: none" : "navigation context");

        OrchestratorTaskPromptContext? taskPromptContext = null;
        try
        {
            if (_taskPromptContext != null)
            {
                taskPromptContext = _taskPromptContext.Compose(
                    projectName,
                    watchPath,
                    req.NavigationContext,
                    context);
                if (taskPromptContext != null)
                {
                    sb.AppendLine(taskPromptContext.PromptBlock);
                    sb.AppendLine();
                    includedBlocks.AddRange(taskPromptContext.IncludedBlocks);
                }
            }
            else if (context?.Kind == OrchestratorContextKey.TaskKind
                     || !string.IsNullOrWhiteSpace(req.NavigationContext?.CurrentTaskKey)
                     || !string.IsNullOrWhiteSpace(req.NavigationContext?.CurrentTaskId))
            {
                includedBlocks.Add("task context: unavailable");
                _logger.LogError(
                    "orchestrator_task_prompt_context_service_missing contextKey={ContextKey} project={Project}",
                    context?.Value ?? "(navigation-only)",
                    projectName);
            }
        }
        catch (Exception ex)
        {
            includedBlocks.Add("task context: unavailable");
            _logger.LogWarning(
                ex,
                "orchestrator_task_prompt_context_lookup_failed contextKey={ContextKey} taskKey={TaskKey} project={Project}; continuing with the explicitly marked degraded context",
                context?.Value ?? "(navigation-only)",
                req.NavigationContext?.CurrentTaskKey ?? context?.TaskKey ?? req.NavigationContext?.CurrentTaskId,
                projectName);
        }

        if (_componentRouting != null)
        {
            var affectedComponent = req.NavigationContext?.AffectedComponent;
            // The host cannot know the affected implementation before the
            // operator describes the problem. Use an explicit component hint
            // when one exists, otherwise resolve from the current message so
            // the routing block is useful on the first proposal turn instead
            // of always reporting an artificial "unresolved" component.
            var routingComponent = string.IsNullOrWhiteSpace(affectedComponent)
                ? req.Text
                : affectedComponent;
            var route = _componentRouting.Resolve(new ComponentRoutingRequest(
                req.NavigationContext?.ObservedSurface ?? req.NavigationContext?.CurrentPage,
                routingComponent,
                projectName));
            sb.AppendLine(ComponentRoutingService.RenderCompact(route));
            sb.AppendLine();
            includedBlocks.Add("component routing");
        }

        var receiptScope = taskPromptContext != null
            || context?.Kind == OrchestratorContextKey.TaskKind
            || !string.IsNullOrWhiteSpace(req.NavigationContext?.CurrentTaskKey)
            || !string.IsNullOrWhiteSpace(req.NavigationContext?.CurrentTaskId)
            ? "task"
            : "project";
        var receiptTaskKey = taskPromptContext?.TaskKey
            ?? req.NavigationContext?.CurrentTaskKey
            ?? context?.TaskKey
            ?? req.NavigationContext?.CurrentTaskId;
        var receiptContextKey = context?.Value
            ?? (receiptScope == "task" && !string.IsNullOrWhiteSpace(receiptTaskKey)
                ? $"task:{projectName}/{receiptTaskKey}"
                : $"project:{projectName}");
        var receipt = new OrchestratorContextReceipt(
            receiptScope,
            receiptContextKey,
            receiptTaskKey,
            includedBlocks,
            DateTime.UtcNow);

        sb.AppendLine("=== CONTEXT INCLUDED WITH THIS REQUEST ===");
        sb.AppendLine($"Scope: {receipt.Scope}");
        sb.AppendLine($"Context key: {receipt.ContextKey}");
        sb.AppendLine($"Blocks: {string.Join(", ", receipt.IncludedBlocks)}");
        sb.AppendLine();

        _logger.LogInformation(
            "orchestrator_chat_prompt_composed contextKey={ContextKey} scope={Scope} taskKey={TaskKey} includedBlocks={IncludedBlocks}",
            receipt.ContextKey,
            receipt.Scope,
            receipt.TaskKey,
            string.Join(",", receipt.IncludedBlocks));

        sb.AppendLine("=== USER MESSAGE ===");
        sb.AppendLine(req.Text);
        sb.AppendLine();

        sb.AppendLine("Reply directly to the user about \"" + projectName + "\". Be concrete and specific.");
        sb.AppendLine("Use the word \"tasks\", not \"jobs\".");
        sb.AppendLine("Use Markdown for structure when helpful (lists, bold, code).");
        sb.AppendLine("Keep it short unless the user asked for depth.");
        return new OrchestratorChatPromptComposition(sb.ToString(), receipt);
    }

    /// <summary>
    /// Render the AUTHORITATIVE project-state snapshot block. The global
    /// session is shared across projects, so without a per-turn snapshot
    /// the model recalls stale counts from a different project (the
    /// "Runbook has 21 jobs" incident, where the 21 actually belonged to
    /// the Agent Task Processor board the user had been chatting about
    /// earlier). The block names the project explicitly, gives an exact
    /// count, breaks it down by lane, and tells the model in two places
    /// to use "tasks" not "jobs" in the user-facing reply.
    ///
    /// Kept as an internal static helper so unit tests can pin the shape
    /// without spinning up a scanner; <see cref="BuildPrompt"/> calls it
    /// with the already-filtered tasks for the active project.
    /// </summary>
    internal static void AppendProjectStateSnapshot(
        StringBuilder sb,
        string projectName,
        IReadOnlyCollection<TaskInfo> tasksForProject)
    {
        sb.AppendLine($"AUTHORITATIVE current state of \"{projectName}\" ({tasksForProject.Count} tasks total):");
        if (tasksForProject.Count == 0)
        {
            sb.AppendLine("  (no tasks)");
        }
        else
        {
            foreach (var sg in tasksForProject.GroupBy(j => j.State).OrderBy(g => g.Key))
            {
                sb.AppendLine($"  {sg.Key}: {sg.Count()}");
            }
        }
        sb.AppendLine("Use these exact numbers. Any counts you remember from earlier in this session are stale and must be ignored.");
        sb.AppendLine("These items are called \"tasks\" (not \"jobs\") in the user-facing vocabulary.");
        sb.AppendLine();
    }

    /// <summary>
    /// Render the per-turn user-preferences block. Reads the live default
    /// CLI / model for the chatting client from <paramref name="store"/>;
    /// if no client id is available (anonymous read path, legacy callers)
    /// or the record has no defaults set, falls back to the same
    /// bootstrap-identity defaults the boot prompt used.
    ///
    /// Kept static + internal so unit tests can pin the shape without
    /// having to spin up a real chat service.
    /// </summary>
    internal static void AppendCurrentUserPreferences(StringBuilder sb, string? clientId, ClientIdentityStore? store)
    {
        string? cli = null;
        string? model = null;

        if (!string.IsNullOrWhiteSpace(clientId) && store is not null)
        {
            var rec = store.Find(clientId!);
            cli = rec?.DefaultCliType;
            model = rec?.DefaultModel;
        }

        // Always fall back so the block is well-formed even on a fresh
        // install with no recorded defaults.
        var (fallbackCli, fallbackModel) = GlobalOrchestratorBootstrap.ResolveBootDefaults(store);
        var effectiveCli = string.IsNullOrWhiteSpace(cli) ? fallbackCli : cli!;
        var effectiveModel = string.IsNullOrWhiteSpace(model) ? fallbackModel : model!;

        sb.AppendLine("=== CURRENT USER PREFERENCES ===");
        sb.AppendLine($"Default CLI: {effectiveCli}");
        sb.AppendLine($"Default model: {effectiveModel}");
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            sb.AppendLine($"Active client id (forward as X-Client-Id when calling /api/* on the user's behalf): {clientId}");
        }
        sb.AppendLine("These supersede any defaults named in the boot prompt for this turn.");
        sb.AppendLine("If the user asks you to create a task without naming a CLI or model, use these.");
        sb.AppendLine();
    }

    /// <summary>
    /// Render the navigation-context block when the frontend sent one. The
    /// rendered text is what the agent reads, so the wording is part of the
    /// contract: it tells the agent how to interpret context-dependent
    /// questions and explicitly forbids inventing a task when none is in
    /// scope. Kept in one place so unit tests can pin the shape.
    /// </summary>
    internal static void AppendNavigationContext(StringBuilder sb, ChatNavigationContext? nav)
    {
        sb.AppendLine("=== NAVIGATION CONTEXT ===");
        if (nav == null
            || (string.IsNullOrWhiteSpace(nav.CurrentPage)
                && string.IsNullOrWhiteSpace(nav.CurrentTaskId)
                && string.IsNullOrWhiteSpace(nav.CurrentTaskKey)
                && string.IsNullOrWhiteSpace(nav.CurrentTaskTitle)
                && string.IsNullOrWhiteSpace(nav.CurrentTaskState)
                && string.IsNullOrWhiteSpace(nav.CurrentLaneFilter)
                && string.IsNullOrWhiteSpace(nav.ViewportTimestamp)
                && string.IsNullOrWhiteSpace(nav.ObservedSurface)
                && string.IsNullOrWhiteSpace(nav.AffectedComponent)
                && string.IsNullOrWhiteSpace(nav.PageRef)
                && string.IsNullOrWhiteSpace(nav.PageTitle)
                && string.IsNullOrWhiteSpace(nav.PageType)
                && string.IsNullOrWhiteSpace(nav.PageExcerpt)))
        {
            sb.AppendLine("No navigation context was sent with this message.");
            sb.AppendLine("If the user asks a context-dependent question (\"what is the current task?\", \"explain this\"), say no specific task is in scope and ask which task they mean. Do NOT invent a task or hallucinate a context.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("The operator's UI state when they sent this message:");
        if (!string.IsNullOrWhiteSpace(nav.CurrentPage)) sb.AppendLine($"  currentPage: {nav.CurrentPage}");
        if (!string.IsNullOrWhiteSpace(nav.CurrentTaskId)) sb.AppendLine($"  currentTaskId: {nav.CurrentTaskId}");
        if (!string.IsNullOrWhiteSpace(nav.CurrentTaskKey)) sb.AppendLine($"  currentTaskKey: {nav.CurrentTaskKey}");
        if (!string.IsNullOrWhiteSpace(nav.CurrentTaskTitle)) sb.AppendLine($"  currentTaskTitle: {nav.CurrentTaskTitle}");
        if (!string.IsNullOrWhiteSpace(nav.CurrentTaskState)) sb.AppendLine($"  currentTaskState: {nav.CurrentTaskState}");
        if (!string.IsNullOrWhiteSpace(nav.CurrentLaneFilter)) sb.AppendLine($"  currentLaneFilter: {nav.CurrentLaneFilter}");
        if (!string.IsNullOrWhiteSpace(nav.ViewportTimestamp)) sb.AppendLine($"  viewportTimestamp: {nav.ViewportTimestamp}");
        if (!string.IsNullOrWhiteSpace(nav.ObservedSurface)) sb.AppendLine($"  observedSurface: {nav.ObservedSurface}");
        if (!string.IsNullOrWhiteSpace(nav.AffectedComponent)) sb.AppendLine($"  affectedComponent: {nav.AffectedComponent}");
        if (!string.IsNullOrWhiteSpace(nav.PageRef)) sb.AppendLine($"  pageRef: {nav.PageRef}");
        if (!string.IsNullOrWhiteSpace(nav.PageTitle)) sb.AppendLine($"  pageTitle: {nav.PageTitle}");
        if (!string.IsNullOrWhiteSpace(nav.PageType)) sb.AppendLine($"  pageType: {nav.PageType}");
        if (!string.IsNullOrWhiteSpace(nav.PageExcerpt)) sb.AppendLine($"  pageExcerpt: {nav.PageExcerpt}");
        sb.AppendLine();
        sb.AppendLine("Use this when interpreting context-dependent questions. When pageRef is set, the operator is asking from THAT repository page; use its title, type, path, and excerpt. When currentTaskKey or currentTaskId is set, the operator is most likely asking about THAT task; answer with its title/state and refer to it by key. When neither pageRef, currentTaskKey, nor currentTaskId is set, do NOT invent one; say no specific page or task is in scope and ask what they mean. Never produce filler tokens or repeated greetings in place of a real answer.");
        sb.AppendLine();
    }

    internal string ResolveWorkingDirectory(string projectName, string watchPath)
    {
        var entry = _scanner.GetWatchPaths().FirstOrDefault(e =>
            string.Equals(e.Path, watchPath, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(entry?.RootPath))
            return entry.RootPath;

        var project = _projects?.FindByStorageLocation(watchPath)
                      ?? _projects?.FindByIdOrDisplayName(projectName);
        if (!string.IsNullOrWhiteSpace(project?.RepositoryPath))
            return project.RepositoryPath;

        var tempPath = Path.GetTempPath();
        _logger.LogWarning(
            "orchestrator_chat_working_directory_temp_fallback project={Project} watchPath={WatchPath} " +
            "reason=missing-watch-root-and-registry-repository-path tempPath={TempPath}",
            projectName, watchPath, tempPath);
        return tempPath;
    }

    /// <summary>
    /// Resolve the checkout identity shown in the chat header. Remote projects
    /// queue a non-mutating host inspection when no exact runner snapshot is
    /// cached yet; local projects read branch and HEAD directly from the same
    /// repository root used by the local Codex one-shot.
    /// </summary>
    public ChatExecutionContext ResolveExecutionContext(string projectName, string watchPath)
    {
        var route = ResolveRemoteRoute(projectName, watchPath);
        if (route != null && _remoteWork != null)
        {
            var observed = _remoteWork.GetContext(route);
            if (observed != null) return observed;
            _remoteWork.RequestInspection(route);
            return new ChatExecutionContext(
                "remote",
                route.RunnerId,
                RepoPath: null,
                route.DefaultBranch,
                HeadSha: null,
                "resolving",
                DateTime.UtcNow);
        }

        var root = ResolveWorkingDirectory(projectName, watchPath);
        return new ChatExecutionContext(
            "local",
            "local",
            root,
            _git?.ReadBranchAt(root),
            _git?.ReadHeadShaAt(root),
            "ready",
            DateTime.UtcNow);
    }

    private RemoteChatWorkRoute? ResolveRemoteRoute(string projectName, string watchPath)
    {
        if (_projectSettings == null || _projects == null) return null;
        var settings = _projectSettings.Get(projectName);
        if (!settings.RemoteExecutionEnabled || string.IsNullOrWhiteSpace(settings.ExecutionRunner))
            return null;

        var project = _projects.FindByStorageLocation(watchPath)
                      ?? _projects.FindByIdOrDisplayName(projectName);
        var repository = RemoteProjectRepositoryResolver.Resolve(project, settings.IntegrationBranch);
        if (repository == null)
            throw new InvalidOperationException(
                $"Remote project chat cannot resolve a repository URL for '{projectName}'.");
        return new RemoteChatWorkRoute(
            settings.ExecutionRunner!,
            repository.ProjectId,
            projectName,
            repository.RepositoryUrl,
            repository.DefaultBranch);
    }

}
