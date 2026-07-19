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
    /// A clean-context invocation always receives a newly-created CODEX_HOME.
    /// CleanContextPreparer deliberately excludes sessions, so no previous
    /// rollout can exist there. Shared-context invocations must prove the
    /// rollout exists before attempting resume.
    /// </summary>
    public static bool CanResume(string? sessionId, string? contextMode, string? sharedHome = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        if (CliContextModes.Normalize(contextMode) == CliContextModes.Clean) return false;
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
