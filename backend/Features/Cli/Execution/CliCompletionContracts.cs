namespace AgentStudio.Cli;

/// <summary>
/// How one CLI backend signals turn completion to the orchestrator. This is
/// the read-only, introspectable view of the per-CLI adapter mappings that
/// already live in <c>Execution/Adapters/*EventAdapter.cs</c>: it describes
/// which native frame each adapter treats as
/// <see cref="CliRunEvent.TurnCompleted"/> / <see cref="CliRunEvent.TurnFailed"/>,
/// where the usage summary is read from, and whether a typed adapter exists
/// at all.
///
/// <para>
/// <b>Why a registry instead of hard-coding the strings in the UI.</b> The
/// Admin/CLI page must show the <em>real</em> completion contract per CLI, not
/// a frontend guess. Sourcing it from one backend record keeps the page honest:
/// if an adapter's completion frame changes, this registry is the single place
/// that must move with it, and the UI follows automatically via
/// <c>GET /api/cli/contracts</c>.
/// </para>
/// </summary>
public sealed record CliCompletionContract
{
    /// <summary>CLI backend id (one of <see cref="CliTypes.All"/>).</summary>
    public required string CliType { get; init; }

    /// <summary>Wire format + invocation the runner parses (e.g. stream-json NDJSON).</summary>
    public required string Transport { get; init; }

    /// <summary>Native frame the adapter maps to <see cref="CliRunEvent.SessionStarted"/>.</summary>
    public required string SessionStartSignal { get; init; }

    /// <summary>Native frame the adapter maps to <see cref="CliRunEvent.TurnCompleted"/>.</summary>
    public required string CompletionSignal { get; init; }

    /// <summary>Native frame the adapter maps to <see cref="CliRunEvent.TurnFailed"/>.</summary>
    public required string FailureSignal { get; init; }

    /// <summary>Where the per-turn usage summary is read from (empty when none).</summary>
    public required string UsageSource { get; init; }

    /// <summary>
    /// True when a typed <see cref="CliRunEvent"/> adapter classifies this CLI's
    /// frames. False means completion is inferred from process exit / heuristics
    /// (Copilot, until its TUI gets a screen-scraping adapter).
    /// </summary>
    public required bool Typed { get; init; }

    /// <summary>One-line free-form note for the UI.</summary>
    public required string Notes { get; init; }
}

/// <summary>
/// The completion contracts for every supported CLI, mirrored from the live
/// adapter code. Static and pure — no I/O, no process — so it is safe to
/// serve directly from the endpoint and to assert against in tests.
/// </summary>
public static class CliCompletionContracts
{
    public static readonly IReadOnlyList<CliCompletionContract> All =
    [
        new CliCompletionContract
        {
            CliType = CliTypes.Claude,
            Transport = "stream-json NDJSON (claude --output-format stream-json --verbose)",
            SessionStartSignal = "system frame, subtype=init",
            CompletionSignal = "result frame, is_error=false",
            FailureSignal = "result frame, is_error=true",
            UsageSource = "result.usage (input / output / cache_read tokens)",
            Typed = true,
            Notes = "ClaudeEventAdapter maps native frames to typed CliRunEvent.",
        },
        new CliCompletionContract
        {
            CliType = CliTypes.Codex,
            Transport = "JSONL (codex exec --json)",
            SessionStartSignal = "thread.started (legacy: session_meta)",
            CompletionSignal = "turn.completed",
            FailureSignal = "turn.failed",
            UsageSource = "turn.completed.usage (input / cached / output / reasoning tokens)",
            Typed = true,
            Notes = "CodexEventAdapter; reasoning items map to Heartbeat liveness pings.",
        },
        new CliCompletionContract
        {
            CliType = CliTypes.Gemini,
            Transport = "stream-json NDJSON (gemini -o stream-json)",
            SessionStartSignal = "init frame",
            CompletionSignal = "result frame, status=success",
            FailureSignal = "result frame, status != success",
            UsageSource = "result.stats (input / output / cached tokens, tool_calls)",
            Typed = true,
            Notes = "GeminiEventAdapter maps native frames to typed CliRunEvent.",
        },
        new CliCompletionContract
        {
            CliType = CliTypes.Copilot,
            Transport = "PTY / TUI (no structured event stream)",
            SessionStartSignal = "none (not surfaced by a typed adapter)",
            CompletionSignal = "process exit (heuristic, exit-based)",
            FailureSignal = "non-zero process exit / watchdog kill",
            UsageSource = "none",
            Typed = false,
            Notes = "No CliRunEvent adapter yet; completion is exit-based. Screen-scraping adapter is planned.",
        },
    ];
}
