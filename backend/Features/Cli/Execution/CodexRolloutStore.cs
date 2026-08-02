using System.Text.RegularExpressions;

namespace AgentStudio.Cli;

/// <summary>
/// Resolves Codex thread ids against the rollout files that <c>codex exec
/// resume</c> actually opens. The session index is presentation metadata only;
/// an index-only id is not resumable.
/// </summary>
public static class CodexRolloutStore
{
    private static readonly Regex RolloutId = new(
        @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\.jsonl$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string ResolveSharedHome()
        => Environment.GetEnvironmentVariable("CODEX_HOME")
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>
    /// Whether <c>codex exec resume &lt;sessionId&gt;</c> can actually open the
    /// referenced rollout in the home the invocation will run against.
    /// <para>
    /// Clean-context invocations run against the task's persistent per-task
    /// home (all attempts of one task share it — see
    /// <c>GenericCliExecutionService.AcquireCleanContext</c>), so a resume is
    /// viable exactly when that home (<paramref name="cleanHome"/>) already
    /// contains the rollout written by a previous attempt. Without a live
    /// per-task home (first attempt, or the home was evicted at the run
    /// boundary) there is nothing to resume: CleanContextPreparer deliberately
    /// excludes the operator's <c>sessions/</c>, so the shared home's rollout
    /// is invisible to a clean run. Shared-context invocations must prove the
    /// rollout exists in the shared home before attempting resume.
    /// </para>
    /// </summary>
    public static bool CanResume(string? sessionId, string? contextMode, string? sharedHome = null, string? cleanHome = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        if (CliContextModes.Normalize(contextMode) == CliContextModes.Clean)
            return !string.IsNullOrWhiteSpace(cleanHome) && HasRollout(cleanHome!, sessionId);
        return HasRollout(sharedHome ?? ResolveSharedHome(), sessionId);
    }

    public static bool HasRollout(string codexHome, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(codexHome) || string.IsNullOrWhiteSpace(sessionId)) return false;
        return EnumerateRolloutIds(codexHome).Contains(sessionId);
    }

    public static HashSet<string> EnumerateRolloutIds(string codexHome)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessionsRoot = Path.Combine(codexHome, "sessions");
        if (!Directory.Exists(sessionsRoot)) return ids;

        try
        {
            foreach (var file in Directory.EnumerateFiles(sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                var match = RolloutId.Match(Path.GetFileName(file));
                if (match.Success) ids.Add(match.Groups[1].Value);
            }
        }
        catch (Exception ex)
        {
            // Session discovery is best-effort. A false negative deliberately
            // chooses a full-context fresh start, which is safer than launching
            // a deterministic no-rollout failure.
            SilentCatch.Note(ex, "CodexRolloutStore: rollout enumeration");
        }
        return ids;
    }
}
