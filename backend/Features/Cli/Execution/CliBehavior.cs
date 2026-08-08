using System.Diagnostics;

namespace AgentStudio.Cli;

/// <summary>
/// Host-side per-CLI descriptor: the data + delegates that customize the
/// single concrete <see cref="GenericCliExecutionService"/> engine for one
/// CLI (Claude Code, Codex, Gemini/Antigravity). This is the host analogue of
/// the library's <c>CodingAgentRunner.Execution.CliDescriptor</c>: instead of
/// a class hierarchy of per-CLI subclasses overriding virtuals, the engine is
/// one concrete class and each CLI supplies a <see cref="CliBehavior"/>.
///
/// <para>
/// Every delegate receives the live engine instance as <c>ctx</c> so the
/// behavior can read engine state (config, logger, tracked processes) and call
/// engine helpers (RaiseRunEvent, BuildConventionContext, DefaultSpawnChildAsync,
/// ...). A null nullable-delegate means "use the engine's built-in default";
/// the two required delegates (<see cref="GetCliPath"/>,
/// <see cref="BuildStartInfo"/>) have no default and must be supplied.
/// </para>
/// </summary>
internal sealed class CliBehavior
{
    // ── Data ────────────────────────────────────────────────────────────

    /// <summary>One of <c>CliTypes</c>. Required.</summary>
    public required string CliType { get; init; }

    /// <summary>Whether this CLI can isolate a clean run via a task-stable config home (T1b).</summary>
    public bool SupportsCleanContext { get; init; }

    /// <summary>Real CLIs emit a session id on every run; a behavior that does not can set this false.</summary>
    public bool EmitsSessionId { get; init; } = true;

    /// <summary>Whether the runner should reconstruct usage post-hoc when a run finished without a usage footer.</summary>
    public bool NeedsPostHocUsageReconstruction { get; init; }

    // ── Required delegates ──────────────────────────────────────────────

    /// <summary>Resolve the executable path/name this CLI runs.</summary>
    public required Func<GenericCliExecutionService, string> GetCliPath { get; init; }

    /// <summary>Build the command-line for one spawn. Required.</summary>
    public required BuildStartInfoDelegate BuildStartInfo { get; init; }

    // ── Optional delegates (null => engine default) ─────────────────────

    /// <summary>Session-name compatibility. Default: any non-empty name.</summary>
    public Func<GenericCliExecutionService, string?, bool>? IsCompatibleSessionName { get; init; }

    /// <summary>Probe the CLI version/availability. Default: a <c>--version</c> probe.</summary>
    public Func<GenericCliExecutionService, string?, (bool Available, string? Version, string Path)>? TestCliPath { get; init; }

    /// <summary>Pre-spawn health/self-heal. Default: a fast <c>--version</c> probe with no repair.</summary>
    public Func<GenericCliExecutionService, CancellationToken, Task<(bool Ok, string? Error)>>? EnsureCliHealthy { get; init; }

    /// <summary>Text to write to the child's stdin. Default null: close stdin immediately.</summary>
    public Func<GenericCliExecutionService, string, string?, bool, string?, string?>? GetPromptStdinPayload { get; init; }

    /// <summary>Normalize a persisted model before invocation. Default: trim / null-if-blank.</summary>
    public Func<GenericCliExecutionService, string?, string?>? NormalizeModelForInvocation { get; init; }

    /// <summary>Per-output-line session-metadata capture hook. Default: no-op.</summary>
    public Action<GenericCliExecutionService, GenericCliExecutionService.ProcInfo, CliOutputLine>? OnOutputLine { get; init; }

    /// <summary>
    /// Capture metadata from one raw protocol line before its typed events are
    /// published. This ordering is load-bearing for the token ledger: its
    /// <c>TurnCompleted</c> subscriber reads the captured usage synchronously.
    /// </summary>
    public Action<GenericCliExecutionService, string, CliOutputLine>? CaptureRawLine { get; init; }

    /// <summary>Map a raw line to zero or more typed run events. Default: none.</summary>
    public MapLineToRunEventsDelegate? MapLineToRunEvents { get; init; }

    /// <summary>Arm a stdout-independent liveness watcher for a fresh run. Default: no-op.</summary>
    public Action<GenericCliExecutionService, GenericCliExecutionService.ProcInfo, bool, string?>? StartSessionLiveness { get; init; }

    /// <summary>Translate a raw read line into buffer lines. Default: pass through.</summary>
    public Func<GenericCliExecutionService, CliOutputLine, IEnumerable<CliOutputLine>>? TransformReadLine { get; init; }

    /// <summary>Return the model catalog. Default: empty default-only catalog.</summary>
    public Func<GenericCliExecutionService, bool, CancellationToken, Task<CliModelCatalog>>? GetModelCatalog { get; init; }

    /// <summary>Spawn the child process. Default: <see cref="Process"/> with redirected pipes.</summary>
    public SpawnChildDelegate? SpawnChild { get; init; }

    /// <summary>Acquire a task-stable clean-context config home. Default: null (shared-only).</summary>
    public Func<GenericCliExecutionService, string, string, CleanContextPreparation?>? PrepareCleanContext { get; init; }

    /// <summary>Describe the context sources a run loaded. Default: convention-only context.</summary>
    public Func<GenericCliExecutionService, string, AgentStudio.Shared.CliExecutionContext?>? DescribeContextSources { get; init; }

    // ── Named delegate types for the awkward signatures ─────────────────

    internal delegate ProcessStartInfo BuildStartInfoDelegate(
        GenericCliExecutionService ctx,
        string prompt,
        string workingDirectory,
        string? sessionName,
        bool resumeSession,
        string? model,
        string? thinkingLevel,
        string? permissionMode);

    internal delegate IEnumerable<CliRunEvent> MapLineToRunEventsDelegate(
        GenericCliExecutionService ctx,
        string jobKey,
        CliOutputLine line);

    internal delegate Task<ChildHandle> SpawnChildDelegate(
        GenericCliExecutionService ctx,
        ProcessStartInfo psi,
        string prompt,
        string? sessionName,
        bool resumeSession,
        string? model,
        CancellationToken ct);
}
