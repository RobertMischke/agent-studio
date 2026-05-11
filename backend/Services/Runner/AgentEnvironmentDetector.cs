using System.Collections.Immutable;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Canonical recognizer for OS-level / sandbox-level blockers in raw CLI
/// output (stdout/stderr). When one of these patterns fires, the agent
/// cannot escape the situation by trying harder - the host environment is
/// refusing to execute the work and only the user can unblock it (relax
/// sandbox config, fix the Windows logon session, grant a permission).
///
/// <para>
/// <b>Why this exists.</b> 2026-05-11: a Codex run on the Lotta dashboard
/// project produced <c>windows sandbox: runner error: CreateProcessAsUserW
/// failed: 1312</c> on every shell call. The agent retried for nine seconds
/// before giving up with a free-text apology and no terminal sentinel; the
/// job landed in 4-auto-review as a generic "missing-terminal-sentinel"
/// case, which masked the real cause. The orchestrator must recognise
/// these blockers in-stream, stop the run quickly, and route the job
/// directly to human review with a typed diagnosis instead of letting the
/// auto-review pipeline spend aspect runs on an empty change set.
/// </para>
///
/// <para>
/// <b>Adding a pattern.</b> Append one line to <see cref="Patterns"/>. Use
/// <see cref="EnvironmentBlockerPattern.ImmediateTerminate"/> = true only
/// when the pattern is unambiguous and a single occurrence is sufficient
/// evidence to abort. Ambiguous patterns ride the default per-run
/// <see cref="HitThreshold"/> instead so a single transient stderr line
/// cannot wedge a healthy run.
/// </para>
/// </summary>
public static class AgentEnvironmentDetector
{
    /// <summary>
    /// Number of pattern hits inside one run before a non-immediate blocker
    /// fires. Codex's repeated retries against the same sandbox error
    /// typically surface the same line 3-10 times in a few seconds; three
    /// is enough to be sure without burning the entire silence budget.
    /// </summary>
    public const int HitThreshold = 3;

    public sealed record EnvironmentBlockerPattern(
        string Id,
        string Needle,
        string ShortLabel,
        string DiagnosisTemplate,
        bool ImmediateTerminate = false);

    /// <summary>
    /// Canonical list of OS / sandbox blockers we know about. Substring
    /// match (case-insensitive). Keep entries short, evidence-based, and
    /// distinct - a vague needle that overlaps with normal agent prose
    /// would cause false positives and is worse than no rule.
    /// </summary>
    public static readonly ImmutableArray<EnvironmentBlockerPattern> Patterns = ImmutableArray.Create(
        new EnvironmentBlockerPattern(
            Id: "codex-windows-sandbox",
            Needle: "windows sandbox: runner error",
            ShortLabel: "Codex Windows sandbox refused to execute commands",
            DiagnosisTemplate: "Codex's Windows sandbox wrapper refused to execute shell commands. The agent cannot resolve this on its own. Either set `sandbox_mode = \"danger-full-access\"` in the Codex config, or reissue the task with explicit instructions to use only file-reading tools.",
            ImmediateTerminate: true),

        new EnvironmentBlockerPattern(
            Id: "windows-logon-1312",
            Needle: "CreateProcessAsUserW failed: 1312",
            ShortLabel: "Windows logon session error 1312",
            DiagnosisTemplate: "Windows refused to start a child process under the current logon session (error 1312, ERROR_NO_SUCH_LOGON_SESSION). This typically means the host's logon session was destroyed (RDP / service-account / sandbox handle issue); the agent cannot self-recover.",
            ImmediateTerminate: true),

        new EnvironmentBlockerPattern(
            Id: "codex-sandbox-permissions",
            Needle: "sandbox_permissions",
            ShortLabel: "Codex sandbox permission misconfiguration",
            DiagnosisTemplate: "Codex reported a `sandbox_permissions` configuration error. Review the project's Codex sandbox config; the agent cannot grant itself the needed permissions."),

        new EnvironmentBlockerPattern(
            Id: "claude-permission-denied-tool",
            Needle: "Permission denied and could not request permission from user",
            ShortLabel: "Tool permission denied (no interactive prompt)",
            DiagnosisTemplate: "A tool invocation was denied and Claude could not surface an interactive permission prompt. Grant the required tool permission up front (settings.json) or rephrase the task to avoid the gated path."),

        new EnvironmentBlockerPattern(
            Id: "posix-eacces",
            Needle: "EACCES",
            ShortLabel: "POSIX permission denied (EACCES)",
            DiagnosisTemplate: "A POSIX permission-denied error (EACCES) blocked the agent. The agent cannot grant itself filesystem permissions; check the affected path's ACLs."),

        new EnvironmentBlockerPattern(
            Id: "posix-eperm",
            Needle: "EPERM",
            ShortLabel: "POSIX operation not permitted (EPERM)",
            DiagnosisTemplate: "A POSIX EPERM error blocked the agent. The host policy is refusing the requested operation; the agent cannot self-elevate."),

        new EnvironmentBlockerPattern(
            Id: "windows-access-denied",
            Needle: "Access is denied",
            ShortLabel: "Windows access denied",
            DiagnosisTemplate: "Windows refused access to the requested resource. The agent cannot grant itself the missing rights; review file ACLs, sandbox config, or run as a user with the needed scope.")
    );

    /// <summary>
    /// True if <paramref name="line"/> matches any registered blocker
    /// pattern. Public so unit tests can lock the pattern list against
    /// regression without spinning up a CLI.
    /// </summary>
    public static bool IsSandboxBlocker(string? line)
        => Match(line) != null;

    /// <summary>
    /// Find the first matching pattern for a single CLI output line, or
    /// null if none. Returns the canonical record so the caller can pick
    /// up <see cref="EnvironmentBlockerPattern.ImmediateTerminate"/>.
    /// </summary>
    public static EnvironmentBlockerPattern? Match(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        foreach (var p in Patterns)
        {
            if (line.IndexOf(p.Needle, StringComparison.OrdinalIgnoreCase) >= 0)
                return p;
        }
        return null;
    }

    /// <summary>
    /// Render a one-paragraph diagnosis for a matched blocker, suitable
    /// for display in the chat log and on the job card tooltip. Includes
    /// the originating CLI type so the suggested recovery is specific.
    /// </summary>
    public static string Diagnose(EnvironmentBlockerPattern pattern, string cliType)
    {
        if (pattern is null) throw new ArgumentNullException(nameof(pattern));
        var cli = string.IsNullOrWhiteSpace(cliType) ? "the agent CLI" : cliType;
        return $"{pattern.ShortLabel} (cli={cli}): {pattern.DiagnosisTemplate}";
    }
}
