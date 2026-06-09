using System.Text.RegularExpressions;
using OrchestratorApi.Services.Registry;

namespace OrchestratorApi.Services.Tasks;

/// <summary>
/// F45a — translates between the canonical F45 jobKey format
/// (<c>PROJ-001::job-slug</c>) and the legacy <c>&lt;path&gt;::&lt;slug&gt;</c>
/// form produced by <see cref="OrchestratorApi.Models.TaskIdentity.CreateKey"/>.
///
/// <para>F45a is additive: existing code still mints legacy keys; this
/// service is the one place that knows both formats so future call sites
/// can adopt the canonical form one at a time without breaking writes
/// already on disk.</para>
///
/// <para>Format detection is deliberately strict to avoid accidental
/// canonicalisation of malformed inputs:</para>
///
/// <list type="bullet">
/// <item><b>Canonical</b>: matches <c>^PROJ-\d{3,}::.+$</c>.</item>
/// <item><b>Legacy</b>: contains <c>::</c> but does not match canonical.
/// The substring before <c>::</c> is treated as the storage path; if it
/// resolves to a registered project, the key is rewritten in canonical
/// form. Unknown paths return null (the caller decides whether to fall
/// back, ignore, or surface the unresolved key).</item>
/// </list>
/// </summary>
public sealed class TaskKeyResolver
{
    private static readonly Regex CanonicalShape = new(
        @"^PROJ-\d{3,}::.+$",
        RegexOptions.Compiled);

    private readonly ProjectRegistry _projects;
    private readonly ILogger<TaskKeyResolver> _logger;

    public TaskKeyResolver(ProjectRegistry projects, ILogger<TaskKeyResolver> logger)
    {
        _projects = projects;
        _logger = logger;
    }

    /// <summary>Returns true when <paramref name="key"/> matches the canonical shape.</summary>
    public static bool IsCanonical(string? key) =>
        !string.IsNullOrEmpty(key) && CanonicalShape.IsMatch(key);

    /// <summary>Composes a canonical key from a project id and job slug.</summary>
    public static string Build(string projectId, string slug)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("projectId is required", nameof(projectId));
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("slug is required", nameof(slug));
        var composed = $"{projectId}::{slug}";
        if (!CanonicalShape.IsMatch(composed))
            throw new ArgumentException($"projectId does not match PROJ-NNN shape: {projectId}", nameof(projectId));
        return composed;
    }

    /// <summary>
    /// Splits a canonical key into its components. Throws when the input
    /// is not in canonical form; callers should test with
    /// <see cref="IsCanonical"/> or use <see cref="ToCanonical"/> first.
    /// </summary>
    public static (string ProjectId, string Slug) Parse(string canonicalKey)
    {
        if (!IsCanonical(canonicalKey))
            throw new FormatException($"Not a canonical jobKey: {canonicalKey}");
        var sep = canonicalKey.IndexOf("::", StringComparison.Ordinal);
        return (canonicalKey[..sep], canonicalKey[(sep + 2)..]);
    }

    /// <summary>
    /// Translates any-form key to canonical, looking up the project by
    /// storage path when the input is legacy <c>&lt;path&gt;::&lt;slug&gt;</c>.
    /// Returns null when the input is empty or the legacy path does not
    /// match a registered project.
    /// </summary>
    public string? ToCanonical(string? anyKey)
    {
        if (string.IsNullOrEmpty(anyKey)) return null;
        if (CanonicalShape.IsMatch(anyKey)) return anyKey;

        var sep = anyKey.IndexOf("::", StringComparison.Ordinal);
        if (sep <= 0 || sep >= anyKey.Length - 2) return null;

        var pathPart = anyKey[..sep];
        var slug = anyKey[(sep + 2)..];
        var project = _projects.FindByStorageLocation(pathPart);
        if (project == null)
        {
            _logger.LogDebug(
                "jobkey-resolver-unknown-storage path={Path} slug={Slug}", pathPart, slug);
            return null;
        }
        return Build(project.Id, slug);
    }

    /// <summary>
    /// Convenience that returns the canonical form when known, or the
    /// original input as a passthrough when no project matches. Useful
    /// for read paths that surface the key even when the project has not
    /// yet been registered.
    /// </summary>
    public string ToCanonicalOrOriginal(string? anyKey)
    {
        if (string.IsNullOrEmpty(anyKey)) return anyKey ?? "";
        var canonical = ToCanonical(anyKey);
        return canonical ?? anyKey;
    }
}
