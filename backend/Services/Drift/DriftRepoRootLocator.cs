namespace OrchestratorApi.Services.Drift;

/// <summary>
/// Resolves the source repository root for drift scope selection by walking up
/// from <see cref="AppContext.BaseDirectory"/> (then the current working
/// directory) until an <c>AGENTS.md</c> marker is found. Shared by the manual
/// <c>DriftReportEndpoints</c> actions and the automatic <c>DriftPostStepRunner</c>
/// so the two trigger paths resolve the same root.
/// </summary>
public static class DriftRepoRootLocator
{
    public static string Resolve()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        var cwd = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (cwd is not null)
        {
            if (File.Exists(Path.Combine(cwd.FullName, "AGENTS.md")))
                return cwd.FullName;
            cwd = cwd.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
