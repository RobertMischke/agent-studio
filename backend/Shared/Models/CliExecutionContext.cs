namespace AgentStudio.Shared;

/// <summary>
/// The set of context sources a CLI loaded for one run, beyond the prompt the
/// runner handed it: memory / session paths, the instruction-file chain, the
/// global config, plus the model, effective permission mode, and working
/// directory. Produced per-run by the adapter contract
/// <c>ICliExecutionService.DescribeContextSources</c> and persisted as
/// <see cref="SessionEvent.ExecutionContext"/> so the run-detail "Execution
/// Context" panel can show what the agent actually saw (ASS-1739 / T1a).
///
/// <para>
/// This is a <b>read-only observability</b> surface. Building it never changes
/// what the CLI loads; policy changes are a separate task (T1b). For Claude the
/// scalar header (<see cref="Model"/>, <see cref="PermissionMode"/>,
/// <see cref="Cwd"/>) is parsed from the stream-json init frame the CLI already
/// emits; for Codex / Gemini it is derived from the adapter
/// invocation plus the CLI's documented config-path conventions.
/// </para>
/// </summary>
public record CliExecutionContext
{
    /// <summary>One of <c>CliTypes</c>: claude / codex / gemini.</summary>
    public string Cli { get; init; } = "";

    /// <summary>Model the run was invoked with, when known.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Effective run-scoped reasoning level selected by RunSpec / model
    /// qualification, when the CLI supports one.
    /// </summary>
    public string? ThinkingLevel { get; init; }

    /// <summary>
    /// Effective permission / sandbox posture for the run. For Claude this is
    /// the CLI's own init-frame term (<c>bypassPermissions</c> / <c>acceptEdits</c>
    /// / <c>plan</c> / <c>default</c>) when available; otherwise the platform
    /// mode the runner resolved (<c>CliPermissionModes</c>).
    /// </summary>
    public string? PermissionMode { get; init; }

    /// <summary>Working directory the CLI ran in (the run's checkout / worktree).</summary>
    public string? Cwd { get; init; }

    /// <summary>
    /// Resolved context mode for the run (T1b / ASS-1742): <c>clean</c> when the
    /// adapter acquired an isolated task-stable config home, or <c>shared</c> when the
    /// run used the operator's global CLI state. Null for runs captured before
    /// the field existed. Under clean, <see cref="Sources"/> point at the temp
    /// home rather than the user profile.
    /// </summary>
    public string? ContextMode { get; init; }

    /// <summary>UTC time this context was captured (at run finish).</summary>
    public DateTime CapturedAt { get; init; }

    /// <summary>
    /// How the context was derived: <c>init-frame</c> (parsed from the CLI's own
    /// startup frame, richest) or <c>convention</c> (adapter invocation + config
    /// path conventions).
    /// </summary>
    public string Source { get; init; } = "";

    /// <summary>The individual context sources, grouped only by their <see cref="CliContextSource.Kind"/>.</summary>
    public List<CliContextSource> Sources { get; init; } = [];
}

/// <summary>
/// One context input the CLI loaded for the run - a memory file, an
/// instruction-file in the upward chain, a session-store directory, the global
/// config, an MCP server, or a relevant environment signal.
/// </summary>
public record CliContextSource
{
    /// <summary>One of <see cref="CliContextSourceKinds"/>.</summary>
    public string Kind { get; init; } = "";

    /// <summary>Short human label, e.g. <c>"Project memory"</c> or <c>"Global config"</c>.</summary>
    public string Label { get; init; } = "";

    /// <summary>Filesystem path (absolute) of the source, when it is a file / directory. Null for non-path signals (e.g. env).</summary>
    public string? Path { get; init; }

    /// <summary>Whether <see cref="Path"/> exists on disk when known; null when not a path or not probed.</summary>
    public bool? Exists { get; init; }

    /// <summary>Optional extra detail, e.g. an MCP server status or an env value summary.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// The closed set of <see cref="CliContextSource.Kind"/> values. Kept as
/// constants so the FE filter / glyph logic and the backend producers agree.
/// </summary>
public static class CliContextSourceKinds
{
    /// <summary>A memory file the CLI auto-loads (Claude CLAUDE.md, Codex AGENTS.md, Gemini GEMINI.md).</summary>
    public const string Memory = "memory";
    /// <summary>An instruction file in the upward project chain (e.g. .github/copilot-instructions.md).</summary>
    public const string InstructionFile = "instruction-file";
    /// <summary>A session / transcript store directory the CLI reads or writes.</summary>
    public const string Session = "session";
    /// <summary>The CLI's global config directory or file (~/.claude, ~/.codex/config.toml, ...).</summary>
    public const string GlobalConfig = "global-config";
    /// <summary>An MCP server wired into the run (from Claude's init frame).</summary>
    public const string Mcp = "mcp";
    /// <summary>A relevant environment signal (e.g. CODEX_HOME, a present auth token).</summary>
    public const string Env = "env";
}
