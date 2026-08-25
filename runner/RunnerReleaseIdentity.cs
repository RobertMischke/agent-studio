using System.Reflection;

namespace AgentRunner;

/// <summary>
/// Resolves the immutable agent-host deployment identity advertised to the
/// Task Server. Production releases are directories below
/// <c>/opt/agent-host/releases</c> selected by the <c>current</c> symlink.
/// </summary>
internal static class RunnerReleaseIdentity
{
    internal static string Current { get; } = Resolve(AppContext.BaseDirectory);

    internal static string Resolve(string baseDirectory, string? configured = null)
    {
        var explicitId = (configured ?? RunnerOptions.Env("RUNNER_RELEASE_ID")).Trim();
        if (explicitId.Length > 0) return explicitId;

        // AppContext.BaseDirectory includes a trailing separator. Preserve the
        // symlink itself instead of crossing `current/` before inspecting it.
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var directory = new DirectoryInfo(fullPath);
        if (string.Equals(directory.Name, "current", StringComparison.Ordinal))
        {
            try
            {
                var target = directory.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null && target.Name.Length > 0) return target.Name;
            }
            catch (IOException)
            {
                // A concurrent promotion may replace the symlink. The assembly
                // identity below remains a truthful bounded fallback.
            }
        }

        if (string.Equals(directory.Parent?.Name, "releases", StringComparison.Ordinal)
            && directory.Name.Length > 0)
            return directory.Name;

        var assembly = typeof(RunnerReleaseIdentity).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? assembly.GetName().Version?.ToString(3)
               ?? "unknown";
    }
}
