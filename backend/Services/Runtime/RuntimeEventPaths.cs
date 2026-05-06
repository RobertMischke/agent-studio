namespace OrchestratorApi.Services.Runtime;

/// <summary>
/// Canonical layout for Product Runtime Observability JSONL files. Mirrors the
/// description in <c>docs/schemas/product-runtime-event.schema.json</c> and the
/// retention rules in <c>docs/product-runtime-log-capture.md</c>.
/// </summary>
/// <remarks>
/// <para>Two scopes:</para>
/// <code>
/// {jobFolder}/logs/runtime/&lt;yyyy-mm-dd&gt;.jsonl                                  // job-scoped
/// {workspaceRoot}/logs/runtime/&lt;project&gt;/&lt;yyyy-mm-dd&gt;.jsonl                // project-scoped
/// {workspaceRoot}/logs/runtime/_workspace/&lt;yyyy-mm-dd&gt;.jsonl                   // workspace-scoped
/// </code>
/// <para>
/// Parse warnings sit next to the source file with a <c>.warnings.jsonl</c>
/// suffix so consumers can still read malformed-line context without
/// rescanning. The runtime tree is intentionally separate from
/// <c>logs/bus/</c>: the schemas are different and the streams must not mix.
/// </para>
/// </remarks>
public static class RuntimeEventPaths
{
    public const string WorkspaceScope = "_workspace";

    public static string JobRuntimeDir(string jobFolderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobFolderPath);
        return Path.Combine(jobFolderPath, "logs", "runtime");
    }

    public static string JobDayFile(string jobFolderPath, DateTime utcDay)
    {
        var name = utcDay.ToUniversalTime().ToString("yyyy-MM-dd") + ".jsonl";
        return Path.Combine(JobRuntimeDir(jobFolderPath), name);
    }

    public static string WorkspaceRuntimeRoot(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.Combine(workspaceRoot, "logs", "runtime");
    }

    public static string WorkspaceProjectDir(string workspaceRoot, string? project)
    {
        var scope = string.IsNullOrWhiteSpace(project) ? WorkspaceScope : project;
        return Path.Combine(WorkspaceRuntimeRoot(workspaceRoot), scope);
    }

    public static string WorkspaceDayFile(string workspaceRoot, string? project, DateTime utcDay)
    {
        var name = utcDay.ToUniversalTime().ToString("yyyy-MM-dd") + ".jsonl";
        return Path.Combine(WorkspaceProjectDir(workspaceRoot, project), name);
    }

    public static string WarningsFile(string jsonlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonlPath);
        return jsonlPath + ".warnings.jsonl";
    }
}
