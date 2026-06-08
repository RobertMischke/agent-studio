using System.Text.Json;
using OrchestratorApi.Services.Tasks;

namespace OrchestratorApi.Services.Pipeline;

/// <summary>
/// Three-mode verdict policy for a configurable post-step (ASS-563 /
/// ASS-526). The mode controls what the runtime does with a step's
/// non-zero exit, not whether the step runs - the deterministic step
/// itself is always cheap enough to execute and record.
/// </summary>
public enum PostStepMode
{
    /// <summary>Skip the step entirely. No timeline event, no log.</summary>
    Off,
    /// <summary>
    /// Run and record the verdict in <c>pipeline-execution.json</c> + a
    /// log file under <c>post-steps/</c>; a non-zero exit never triggers a
    /// reissue. Default for new projects so the gate is observable before
    /// it becomes load-bearing.
    /// </summary>
    Warn,
    /// <summary>
    /// Run and reissue back to <c>2-ready</c> on non-zero exit. Caps at
    /// one reissue per job per post-step to avoid an infinite spin when
    /// the agent cannot clear the gate.
    /// </summary>
    Fail,
}

/// <summary>
/// Per-step mode resolved at runtime by walking the configuration layers:
/// per-task override (<c>task.json</c> &gt;<c>postSteps</c>), per-project
/// default (<c>appsettings.Local.json</c> &gt;
/// <c>PostSteps:{StepId}:DefaultMode</c>), and built-in default
/// (<see cref="PostStepConfigResolver.BuiltInDefault"/>). The first layer
/// that produces a valid mode wins.
///
/// <para>
/// The per-task-type override (<c>taskTypeDefaults.feature.postSteps</c>)
/// referenced in the spec is wired through the same <c>PostSteps</c>
/// config section once the orchestrator queries it; the resolver accepts
/// an optional <c>taskTypeMode</c> input so the call site can layer it in
/// without the resolver caring where it came from.
/// </para>
/// </summary>
public static class PostStepConfigResolver
{
    /// <summary>
    /// Built-in default for any post-step the configuration does not name.
    /// <see cref="PostStepMode.Warn"/> matches the rollout policy in
    /// ASS-563: every project starts seeing the verdict without the gate
    /// becoming load-bearing on day one.
    /// </summary>
    public const PostStepMode BuiltInDefault = PostStepMode.Warn;

    /// <summary>
    /// Resolve the mode for a given step on a given job. Reads, in order:
    /// 1. <paramref name="jobFolderPath"/>/task.json -&gt; postSteps.{stepId}
    /// 2. <paramref name="taskTypeMode"/> (caller-supplied; lets task-type
    ///    defaults plug in without this resolver doing schema-aware reads)
    /// 3. <paramref name="projectMode"/> (caller-supplied; e.g. from
    ///    <c>IConfiguration["PostSteps:{stepId}:DefaultMode"]</c>)
    /// 4. <see cref="BuiltInDefault"/>
    ///
    /// Unknown / unparseable values are ignored so a typo on disk falls
    /// through to the next layer rather than crashing the post-step.
    /// </summary>
    public static PostStepMode Resolve(
        string jobFolderPath,
        string stepId,
        PostStepMode? taskTypeMode = null,
        PostStepMode? projectMode = null)
    {
        var jobMode = ReadJobOverride(jobFolderPath, stepId);
        if (jobMode.HasValue) return jobMode.Value;
        if (taskTypeMode.HasValue) return taskTypeMode.Value;
        if (projectMode.HasValue) return projectMode.Value;
        return BuiltInDefault;
    }

    /// <summary>
    /// Convenience overload that reads the project default from
    /// <paramref name="configuration"/> under
    /// <c>PostSteps:{stepId}:DefaultMode</c>. Tests that want to bypass
    /// configuration use the typed overload above directly.
    /// </summary>
    public static PostStepMode Resolve(
        IConfiguration configuration,
        string jobFolderPath,
        string stepId)
    {
        var projectRaw = configuration[$"PostSteps:{stepId}:DefaultMode"];
        var projectMode = ParseMode(projectRaw);
        return Resolve(jobFolderPath, stepId, taskTypeMode: null, projectMode: projectMode);
    }

    /// <summary>
    /// Parse a textual mode token to the enum. Accepts case-insensitive
    /// <c>off</c>, <c>warn</c>, <c>fail</c>; null/empty/unknown all return
    /// null so the caller can fall through to the next config layer.
    /// </summary>
    public static PostStepMode? ParseMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "off"  => PostStepMode.Off,
            "warn" => PostStepMode.Warn,
            "fail" => PostStepMode.Fail,
            _ => null,
        };
    }

    /// <summary>
    /// Read <c>task.json</c> at the given folder and extract
    /// <c>postSteps[stepId]</c>. Returns null when the file is missing,
    /// the field is absent, or the value is not a recognised mode token.
    /// </summary>
    internal static PostStepMode? ReadJobOverride(string jobFolderPath, string stepId)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath) || string.IsNullOrWhiteSpace(stepId)) return null;
        var path = Path.Combine(jobFolderPath, "task.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, TaskJsonFile.ReadOpts);
            if (doc == null) return null;
            if (!doc.TryGetValue("postSteps", out var postSteps)) return null;
            if (postSteps.ValueKind != JsonValueKind.Object) return null;
            // task.json uses camelCase, but step ids already are lower-case
            // tokens (e.g. "post-lint-scss") so we read them literally.
            // Accept either the full pipeline-step id or the bare suffix
            // (e.g. "lint-scss") so per-task config stays terse.
            if (TryReadValue(postSteps, stepId, out var directValue))
            {
                return ParseMode(directValue);
            }
            const string prefix = "post-";
            if (stepId.StartsWith(prefix, StringComparison.Ordinal))
            {
                var bare = stepId.Substring(prefix.Length);
                if (TryReadValue(postSteps, bare, out var bareValue))
                {
                    return ParseMode(bareValue);
                }
            }
            return null;
        }
        catch
        {
            // A malformed task.json should not crash the post-step; fall
            // through to the next config layer.
            return null;
        }
    }

    private static bool TryReadValue(JsonElement obj, string key, out string? value)
    {
        if (obj.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString();
            return true;
        }
        value = null;
        return false;
    }
}
