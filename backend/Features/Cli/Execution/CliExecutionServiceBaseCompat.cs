using System.Diagnostics;

namespace AgentStudio.Cli;

/// <summary>
/// Backward-compatibility base for the old subclass-override CLI model. The
/// production engine is now the concrete, descriptor-driven
/// <see cref="GenericCliExecutionService"/>; the three production shims
/// (Claude / Codex / Antigravity) derive from it directly and supply a
/// <see cref="CliBehavior"/>.
///
/// <para>
/// This abstract type preserves the historic <c>CliExecutionServiceBase</c>
/// surface — the <c>(ILogger, IConfiguration)</c> constructor plus the
/// <c>abstract CliType / GetCliPath / BuildStartInfo</c> override points and
/// the static <c>ResolveExecutable</c> / <c>BuildStartedLineText</c> helpers —
/// so external code and test doubles that still subclass
/// <c>CliExecutionServiceBase</c> keep compiling unchanged. It bridges the
/// subclass's overrides into a <see cref="CliBehavior"/> whose delegates call
/// back into <c>this</c> (the engine instance) via the <c>ctx</c> argument.
/// </para>
/// </summary>
public abstract class CliExecutionServiceBase : GenericCliExecutionService
{
    protected CliExecutionServiceBase(ILogger logger, IConfiguration configuration)
        : base(BuildCompatBehavior(), logger, configuration)
    {
    }

    /// <summary>The CLI type. Subclasses override; bridged into the engine via a resolver.</summary>
    public new abstract string CliType { get; }

    /// <summary>The CLI path. Subclasses override; bridged into the engine.</summary>
    public new abstract string GetCliPath();

    /// <summary>
    /// Build the command-line for this CLI. Subclasses override; bridged into
    /// the engine's <c>BuildStartInfo</c> via the compat behavior delegate.
    /// </summary>
    protected new abstract ProcessStartInfo BuildStartInfo(
        string prompt,
        string workingDirectory,
        string? sessionName,
        bool resumeSession,
        string? model,
        string? thinkingLevel,
        string? permissionMode);

    /// <summary>
    /// The compat behavior whose delegates re-enter the subclass overrides
    /// through the <c>ctx</c> engine instance. All hooks the legacy model
    /// exposed as overridable virtuals are wired here so a subclass that does
    /// not override them gets the same engine defaults as before.
    /// </summary>
    private static CliBehavior BuildCompatBehavior() => new CliBehavior
    {
        // Placeholder; the engine prefers CliTypeResolver below. Subclass
        // CliType is a per-instance abstract override not known until after
        // construction, so it is resolved lazily through ctx.
        CliType = "compat",
        CliTypeResolver = ctx => ((CliExecutionServiceBase)ctx).CliType,
        GetCliPath = ctx => ((CliExecutionServiceBase)ctx).GetCliPath(),
        BuildStartInfo = (ctx, prompt, cwd, session, resume, model, thinking, perm)
            => ((CliExecutionServiceBase)ctx).BuildStartInfo(prompt, cwd, session, resume, model, thinking, perm),
    };

    // ── Static helpers preserved under the historic name ─────────────────

    /// <summary>
    /// Preserved for external callers that reference
    /// <c>CliExecutionServiceBase.ResolveExecutable</c>. Forwards to the engine.
    /// </summary>
    public static new string ResolveExecutable(string nameOrPath)
        => GenericCliExecutionService.ResolveExecutable(nameOrPath);

    /// <summary>
    /// Preserved for callers that reference
    /// <c>CliExecutionServiceBase.BuildStartedLineText</c>. Forwards to the engine.
    /// </summary>
    internal static new string BuildStartedLineText(
        string cliType,
        int processId,
        string? model,
        string? thinkingLevel,
        string? sessionName,
        bool resumeSession)
        => GenericCliExecutionService.BuildStartedLineText(
            cliType, processId, model, thinkingLevel, sessionName, resumeSession);
}
