using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Supervisor;

namespace OrchestratorApi.Services.Bus;

// JobPaths is internal in the OrchestratorApi.Services namespace; same assembly,
// so the bridge can reference it without exposing it publicly.

/// <summary>
/// Single bridge that mirrors existing structured signals (orchestrator chat
/// log, supervisor advisories and interventions, run lifecycle, user prompts,
/// orchestrator token usage) into <see cref="AgentMessageBusStore"/> as typed
/// <see cref="AgentMessage"/> records.
/// </summary>
/// <remarks>
/// <para>
/// V1 is a derived, append-only projection over the raw streams that already
/// exist (<c>cli-output.log</c>, <c>observations.jsonl</c>,
/// <c>interventions.jsonl</c>, <c>orchestrator.jsonl</c>). Those raw streams
/// remain canonical: every legacy reader keeps reading them unchanged. The
/// bus messages reference the originating raw line via an
/// <c>artifact:log-slice</c> when applicable so reviewers can drill from a
/// typed message back to the underlying evidence.
/// </para>
/// <para>
/// All bridge calls are best-effort. Workspace not configured? Append fails?
/// We log and move on - the bus is observability, not authority. The producers
/// are unaware of any bus failure and their canonical writes are unaffected.
/// </para>
/// </remarks>
public sealed class AgentMessageBusBridge
{
    public const string ParticipantRuntime    = "runtime:taskboard";
    public const string ParticipantUser       = "user";
    public const string ParticipantOrchestrator = "orchestrator";
    public const string ParticipantSystemReview = "system-review";

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    private readonly AgentMessageBusStore _store;
    private readonly IConfiguration _config;
    private readonly ILogger<AgentMessageBusBridge> _logger;
    private readonly TimeProvider _time;
    private int _participantsSeeded;

    public AgentMessageBusBridge(
        AgentMessageBusStore store,
        IConfiguration config,
        ILogger<AgentMessageBusBridge> logger,
        TimeProvider? time = null)
    {
        _store = store;
        _config = config;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Stable run-id derivation used across producers so messages emitted on
    /// behalf of one CLI invocation share the same <c>runId</c>. The runs
    /// endpoint surfaces runs by chronological index; the bridge keys off
    /// <c>(jobId, startedAtUtcTicks)</c> so it does not need to consult the
    /// timeline builder.
    /// </summary>
    public static string DeriveRunId(string jobId, DateTime startedAtUtc)
    {
        var ticks = DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc).Ticks;
        return $"{jobId}:{ticks}";
    }

    public static string ParticipantForCli(string? cliType)
    {
        var slug = string.IsNullOrWhiteSpace(cliType) ? "unknown" : cliType.Trim().ToLowerInvariant();
        return $"agent:{slug}";
    }

    public static string ParticipantSupervisor(string project) => $"supervisor:{project}";
    public static string ParticipantOrchestratorFor(string project) => $"orchestrator:{project}";

    /// <summary>
    /// Returns the workspace root, or null when <c>TaskRepository</c> is not
    /// configured. The bus is workspace-scoped so without a root we cannot
    /// emit; callers see this as a no-op.
    /// </summary>
    private string? Workspace() => _config["TaskRepository"];

    private static string NewId() => Guid.CreateVersion7().ToString("N");

    /// <summary>One-time registration of the built-in participant set.</summary>
    public async Task SeedBuiltInParticipantsAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _participantsSeeded, 1, 0) != 0) return;
        var ws = Workspace();
        if (string.IsNullOrWhiteSpace(ws)) return;

        var participants = new[]
        {
            new AgentParticipant { Id = ParticipantUser, Kind = "User", DisplayName = "You" },
            new AgentParticipant { Id = ParticipantRuntime, Kind = "Runtime", DisplayName = "Taskboard runtime" },
            new AgentParticipant { Id = ParticipantOrchestrator, Kind = "Orchestrator", DisplayName = "Orchestrator" },
            new AgentParticipant { Id = "agent:claude", Kind = "CodingAgent", DisplayName = "Claude", Cli = "claude" },
            new AgentParticipant { Id = "agent:codex", Kind = "CodingAgent", DisplayName = "Codex", Cli = "codex" },
            new AgentParticipant { Id = "agent:copilot", Kind = "CodingAgent", DisplayName = "Copilot", Cli = "copilot" },
            new AgentParticipant { Id = "agent:gemini", Kind = "CodingAgent", DisplayName = "Gemini", Cli = "gemini" },
        };
        foreach (var p in participants)
        {
            try { await _store.RegisterParticipantAsync(ws!, p, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus participant seed skipped for {Id}", p.Id); }
        }
    }

    /// <summary>
    /// Mirror an orchestrator chat-log line. Mapping:
    /// <c>Decision -&gt; decision/Info</c>, <c>Reissue -&gt; decision/Warn</c>,
    /// <c>HeuristicFallback -&gt; decision/Warn</c>, <c>GiveUp -&gt; decision/High</c>.
    /// </summary>
    public Task EmitOrchestratorChatAsync(JobInfo info, OrchestratorMessageKind kind, string text, CancellationToken ct = default)
    {
        if (info == null) return Task.CompletedTask;
        var severity = kind switch
        {
            OrchestratorMessageKind.GiveUp            => "High",
            OrchestratorMessageKind.Reissue           => "Warn",
            OrchestratorMessageKind.HeuristicFallback => "Warn",
            OrchestratorMessageKind.Steer             => "Warn",
            _                                         => "Info"
        };
        var topic = kind.ToString().ToLowerInvariant();

        var msg = NewMessage(
            participantId: ParticipantOrchestratorFor(info.ProjectName),
            role: "actor",
            kind: "decision",
            severity: severity,
            project: info.ProjectName,
            jobId: info.Id,
            topic: topic,
            summary: TruncateSummary(text),
            body: text,
            artifacts: new[] { LogSliceArtifact(info) },
            tags: new[] { "orchestrator-chat", topic });

        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Mirror a supervisor chat-log line (the <c>[supervisor]</c>-stream lines
    /// the chat log writes for user-visible interventions like <c>cancel-run</c>,
    /// <c>force-fail</c>, <c>chat-note</c>, <c>escalate</c>,
    /// <c>cycle-resume-failed</c>). One bus message per line.
    /// </summary>
    public Task EmitSupervisorChatAsync(JobInfo info, string tag, string text, CancellationToken ct = default)
    {
        if (info == null) return Task.CompletedTask;
        var msg = NewMessage(
            participantId: ParticipantSupervisor(info.ProjectName),
            role: "actor",
            kind: tag is "cancel-run" or "force-fail" ? "intervention" : "advisory",
            severity: tag is "cycle-resume-failed" or "escalate" ? "High" : "Info",
            project: info.ProjectName,
            jobId: info.Id,
            topic: tag,
            summary: TruncateSummary($"[{tag}] {text}"),
            body: text,
            artifacts: new[] { LogSliceArtifact(info) },
            tags: new[] { "supervisor-chat", tag });

        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Mirror a typed <see cref="SupervisorAdvisory"/> from the hard-health and
    /// soft-reasoning writers. The legacy <c>observations.jsonl</c> record stays
    /// canonical; the bus message references it via an
    /// <c>artifact:supervisor-advisory</c> pointer so a reviewer can pivot from
    /// the timeline back to the originating record.
    /// </summary>
    public Task EmitAdvisoryAsync(SupervisorAdvisory advisory, CancellationToken ct = default)
    {
        if (advisory == null) return Task.CompletedTask;
        var severity = advisory.Severity switch
        {
            SupervisorSeverity.High => "High",
            SupervisorSeverity.Warn => "Warn",
            _                       => "Info"
        };
        var msg = NewMessage(
            participantId: ParticipantSupervisor(advisory.Project),
            role: "evidence",
            kind: "advisory",
            severity: severity,
            project: advisory.Project,
            jobId: advisory.JobId,
            topic: advisory.Topic,
            summary: TruncateSummary(advisory.Message),
            body: advisory.Message,
            createdAt: advisory.CreatedAt,
            artifacts: new[] { SupervisorAdvisoryArtifact(advisory) },
            payload: new { source = advisory.Source.ToString(), advisory.Topic },
            tags: new[] { "supervisor-advisory", advisory.Source.ToString().ToLowerInvariant() });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Mirror a <see cref="SupervisorIntervention"/>. The intervention only
    /// records intent + reason; the actual side effect was applied by the
    /// runner via <see cref="OrchestratorApi.Services.TaskRunnerService.StopJob"/>
    /// or <c>SetMode</c>. We tag the message accordingly so the timeline shows
    /// what was triggered and by whom.
    /// </summary>
    public Task EmitInterventionAsync(SupervisorIntervention intervention, CancellationToken ct = default)
    {
        if (intervention == null) return Task.CompletedTask;
        var severity = intervention.Kind switch
        {
            SupervisorInterventionKind.ForceFail   => "High",
            SupervisorInterventionKind.CancelRun   => "Warn",
            SupervisorInterventionKind.PausePickup => "Warn",
            _                                      => "Info"
        };
        var msg = NewMessage(
            participantId: ParticipantSupervisor(intervention.Project),
            role: "actor",
            kind: "intervention",
            severity: severity,
            project: intervention.Project,
            jobId: intervention.JobId,
            topic: intervention.Kind.ToString(),
            summary: TruncateSummary($"{intervention.Kind}: {intervention.Reason}"),
            body: intervention.Reason,
            createdAt: intervention.CreatedAt,
            artifacts: new[] { SupervisorInterventionArtifact(intervention) },
            payload: new
            {
                source = intervention.Source.ToString(),
                kind = intervention.Kind.ToString(),
                pauseTtlSeconds = intervention.PauseTtl?.TotalSeconds,
            },
            tags: new[] { "supervisor-intervention", intervention.Kind.ToString().ToLowerInvariant() });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Mirror a user prompt or follow-up that the runtime persisted to the
    /// CLI output log. Recorded as <c>kind:question</c> from
    /// <see cref="ParticipantUser"/>; this is the message every later orchestrator
    /// or agent reply chains back to via <c>correlationId</c>.
    /// </summary>
    public Task EmitUserPromptAsync(JobInfo info, string prompt, string mode, CancellationToken ct = default)
    {
        if (info == null) return Task.CompletedTask;
        var trimmed = (prompt ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        var msg = NewMessage(
            participantId: ParticipantUser,
            role: "actor",
            kind: "question",
            severity: "Info",
            project: info.ProjectName,
            jobId: info.Id,
            topic: "user-followup",
            summary: TruncateSummary(trimmed.Length == 0 ? "(empty follow-up)" : trimmed),
            body: prompt,
            artifacts: new[] { LogSliceArtifact(info) },
            payload: new { mode },
            tags: new[] { "user-prompt", $"mode:{mode}" });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Run lifecycle: a fresh CLI process started for the job.
    /// </summary>
    public Task EmitRunStartedAsync(JobInfo info, string cliType, DateTime startedAtUtc, string? sessionId, string intent, CancellationToken ct = default)
    {
        if (info == null) return Task.CompletedTask;
        var runId = DeriveRunId(info.Id, startedAtUtc);
        var msg = NewMessage(
            participantId: ParticipantRuntime,
            role: "system",
            kind: "lifecycle",
            severity: "Info",
            project: info.ProjectName,
            jobId: info.Id,
            runId: runId,
            cliSessionId: sessionId,
            topic: "RunStarted",
            summary: TruncateSummary($"Run started ({cliType}, intent={intent})"),
            createdAt: startedAtUtc,
            artifacts: new[] { LogSliceArtifact(info) },
            payload: new { cliType, intent },
            tags: new[] { "run-lifecycle", "run-started", $"cli:{cliType}" });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Run lifecycle: the CLI process finished. <paramref name="status"/> is
    /// the <c>CliExecution.Status</c> string (<c>completed</c>,
    /// <c>failed</c>, <c>cancelled</c>, ...).
    /// </summary>
    public Task EmitRunFinishedAsync(JobInfo info, string cliType, DateTime startedAtUtc, string status, double? durationSeconds, string? agentOutcome, CancellationToken ct = default)
    {
        if (info == null) return Task.CompletedTask;
        var severity = status switch
        {
            "failed"    => "High",
            "cancelled" => "Warn",
            _           => "Info"
        };
        var runId = DeriveRunId(info.Id, startedAtUtc);
        var msg = NewMessage(
            participantId: ParticipantRuntime,
            role: "system",
            kind: "lifecycle",
            severity: severity,
            project: info.ProjectName,
            jobId: info.Id,
            runId: runId,
            topic: "RunFinished",
            summary: TruncateSummary($"Run {status} ({cliType}{(agentOutcome != null ? $", outcome={agentOutcome}" : string.Empty)})"),
            artifacts: new[] { LogSliceArtifact(info) },
            payload: new { status, cliType, durationSeconds, agentOutcome },
            tags: new[] { "run-lifecycle", $"run-{status}", $"cli:{cliType}" });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Run lifecycle: a stop was requested by the user, the watchdog, or an
    /// auto-mode policy. The actual termination still flows through
    /// <c>RunFinished</c>; this records the trigger.
    /// </summary>
    public Task EmitRunStopRequestedAsync(JobInfo info, RunStopReason reason, string source, CancellationToken ct = default)
    {
        if (info == null) return Task.CompletedTask;
        var msg = NewMessage(
            participantId: ParticipantRuntime,
            role: "system",
            kind: "lifecycle",
            severity: "Warn",
            project: info.ProjectName,
            jobId: info.Id,
            topic: "RunStopRequested",
            summary: TruncateSummary($"Stop requested ({reason}) by {source}"),
            payload: new { reason = reason.ToString(), source },
            tags: new[] { "run-lifecycle", "run-stop-requested", $"reason:{reason}".ToLowerInvariant() });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Job-folder state lane transition (e.g. <c>3-progress -&gt; 4-review</c>,
    /// <c>1-preparation -&gt; 2-ready</c>). One <c>kind:lifecycle</c> message
    /// per actual transition the runtime applies.
    /// </summary>
    public Task EmitJobLifecycleAsync(JobInfo info, string topic, string? fromState, string? toState, string? reason, CancellationToken ct = default)
    {
        if (info == null) return Task.CompletedTask;
        var msg = NewMessage(
            participantId: ParticipantRuntime,
            role: "system",
            kind: "lifecycle",
            severity: "Info",
            project: info.ProjectName,
            jobId: info.Id,
            topic: topic,
            summary: TruncateSummary($"{topic}: {fromState ?? "?"} -> {toState ?? "?"}{(reason is null ? string.Empty : $" ({reason})")}"),
            payload: new { fromState, toState, reason },
            tags: new[] { "job-lifecycle", topic.ToLowerInvariant() });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Token-usage attribution for one orchestrator turn or supporting-agent
    /// call. The aggregate rollup view stays in <c>orchestrator.jsonl</c> /
    /// the token summary service; the bus carries one event per recorded
    /// usage so the timeline shows which turn was expensive.
    /// </summary>
    public Task EmitTokenUsageAsync(string project, string? jobId, string participantId, string? topic, OrchestratorTokenUsage usage, CancellationToken ct = default)
    {
        if (usage == null) return Task.CompletedTask;
        var input  = (long)usage.InputTokens;
        var output = (long)usage.OutputTokens;
        var cacheRead   = (long)usage.CacheReadTokens;
        var cacheWrite  = (long)usage.CacheCreationTokens;

        var tokens = new AgentMessageTokens(
            Input: input,
            Output: output,
            CacheRead: cacheRead == 0 ? null : cacheRead,
            CacheWrite: cacheWrite == 0 ? null : cacheWrite,
            Model: usage.Model,
            Dollars: null);

        var msg = NewMessage(
            participantId: participantId,
            role: "evidence",
            kind: "token-usage",
            severity: "Info",
            project: project,
            jobId: jobId,
            topic: topic ?? "orchestrator-turn",
            summary: TruncateSummary($"tokens: in={input} out={output} model={usage.Model ?? "?"}"),
            tokens: tokens,
            tags: new[] { "token-usage" });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Free-form structured event emit. Awaitable, so tests and future
    /// producers that want backpressure can chain off the result. Production
    /// callers should discard the task (<c>_ = bridge.EmitAsync(...)</c>) so
    /// the canonical write path is never slowed by bus I/O.
    /// </summary>
    /// <remarks>
    /// Append failures are caught and logged - they never propagate. The bus
    /// is observability; a write failure must not break the producer.
    /// </remarks>
    public async Task EmitAsync(AgentMessage message, CancellationToken ct = default)
    {
        var ws = Workspace();
        if (string.IsNullOrWhiteSpace(ws))
        {
            _logger.LogDebug("Bus emit skipped: TaskRepository not configured ({Kind} {Topic})", message.Kind, message.Topic);
            return;
        }
        try { await _store.AppendAsync(ws!, message, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Bus append failed: kind={Kind} topic={Topic} job={Job}", message.Kind, message.Topic, message.JobId); }
    }

    private AgentMessage NewMessage(
        string participantId,
        string role,
        string kind,
        string? severity,
        string? project,
        string? jobId = null,
        string? runId = null,
        string? cliSessionId = null,
        string? topic = null,
        string? summary = null,
        string? body = null,
        DateTime? createdAt = null,
        object? payload = null,
        AgentMessageTokens? tokens = null,
        IReadOnlyList<AgentArtifactRef>? artifacts = null,
        IReadOnlyList<string>? tags = null,
        string? correlationId = null,
        string? replyToId = null)
    {
        var ts = createdAt ?? _time.GetUtcNow().UtcDateTime;
        if (ts.Kind != DateTimeKind.Utc) ts = DateTime.SpecifyKind(ts, DateTimeKind.Utc);
        JsonElement? payloadElement = null;
        if (payload != null)
        {
            var json = JsonSerializer.SerializeToElement(payload, PayloadOptions);
            payloadElement = json;
        }
        return new AgentMessage
        {
            Id = NewId(),
            CreatedAt = ts,
            ParticipantId = participantId,
            Role = role,
            Kind = kind,
            Severity = severity,
            Project = project,
            JobId = jobId,
            RunId = runId,
            CliSessionId = cliSessionId,
            Topic = topic,
            Summary = summary,
            Body = body,
            Tokens = tokens,
            Artifacts = artifacts,
            Payload = payloadElement,
            Tags = tags,
            CorrelationId = correlationId,
            ReplyToId = replyToId,
        };
    }

    private static string TruncateSummary(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        return s.Length <= 280 ? s : s[..277] + "...";
    }

    private static AgentArtifactRef LogSliceArtifact(JobInfo info)
    {
        return new AgentArtifactRef
        {
            Kind = "log-slice",
            Uri = JobPaths.CliOutputLog(info.FolderPath),
            Label = "cli-output.log",
        };
    }

    private static AgentArtifactRef SupervisorAdvisoryArtifact(SupervisorAdvisory a)
    {
        return new AgentArtifactRef
        {
            Kind = "supervisor-advisory",
            Uri = $"observations.jsonl#{a.Project}",
            Label = a.Topic,
        };
    }

    private static AgentArtifactRef SupervisorInterventionArtifact(SupervisorIntervention i)
    {
        return new AgentArtifactRef
        {
            Kind = "supervisor-intervention",
            Uri = $"interventions.jsonl#{i.Project}",
            Label = i.Kind.ToString(),
        };
    }
}
