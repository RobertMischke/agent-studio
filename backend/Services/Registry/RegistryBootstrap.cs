using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Services.Registry;

/// <summary>
/// F45a — boot-time discovery pass. Runs once at startup before the
/// app accepts requests. Populates the workspace + project registries
/// from the configured <c>WatchPaths</c> so the new identity layer is
/// load-bearing the first time the API serves
/// <c>GET /api/workspaces</c> or <c>GET /api/projects</c>.
///
/// <para>F45a is additive: this pass does not write to watched project
/// folders, does not move jobs, and does not rename anything on disk.
/// It only writes <c>&lt;TaskRepository&gt;/.metadata/workspaces.json</c>
/// and <c>projects.json</c>.</para>
///
/// <para>F45c will extend the bootstrap with the migration steps
/// (task-key assignment, lane-folder restructure, project.json backlink
/// inside each watched folder).</para>
/// </summary>
public static class RegistryBootstrap
{
    public static void Run(
        WorkspaceRegistry workspaces,
        ProjectRegistry projects,
        JobScannerService scanner,
        ILogger logger,
        TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;

        if (!workspaces.IsPersistent || !projects.IsPersistent)
        {
            logger.LogInformation(
                "registry-bootstrap-skipped reason=no-task-repository - registries operate in-memory only");
            return;
        }

        var defaultWorkspace = workspaces.EnsureDefaultWorkspace(clock);

        var watchPaths = scanner.GetWatchPaths();
        var discovered = 0;
        var divergedDisplayNames = 0;
        foreach (var entry in watchPaths)
        {
            if (string.IsNullOrWhiteSpace(entry.Path)) continue;
            // Skip paths that don't exist on disk - they may be holdovers
            // from a different machine's appsettings.Local.json and would
            // otherwise pollute the registry with phantom entries.
            if (!Directory.Exists(entry.Path)) continue;

            var existing = projects.FindByStorageLocation(entry.Path);
            if (existing != null)
            {
                // F47 / ADR-0042: WatchPaths is BOOTSTRAP-only after the registry
                // exists. If the operator edited the appsettings entry's Name
                // after the registry recorded a different DisplayName, the
                // registry wins. Surface the divergence in the log so the
                // mismatch is observable.
                if (!string.IsNullOrWhiteSpace(entry.Name)
                    && !string.Equals(entry.Name, existing.DisplayName, StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "registry-bootstrap-watchpath-name-diverges projectId={Id} registryName={RegistryName} watchPathName={WatchPathName} storage={Storage} - registry wins (use the F47 Settings panel to rename)",
                        existing.Id, existing.DisplayName, entry.Name, entry.Path);
                    divergedDisplayNames++;
                }
                continue;
            }

            var displayName = !string.IsNullOrWhiteSpace(entry.Name)
                ? entry.Name
                : InferDisplayNameFromPath(entry.Path);

            projects.EnsureProjectForStorage(
                storageLocation: entry.Path,
                initialDisplayName: displayName,
                workspaceId: defaultWorkspace.Id,
                clock: clock);
            discovered++;
        }

        logger.LogInformation(
            "registry-bootstrap-complete workspaces={WorkspaceCount} projectsDiscovered={Discovered} projectsTotal={Total} watchPathDisplayNameDivergences={Divergences}",
            workspaces.List().Count,
            discovered,
            projects.List().Count,
            divergedDisplayNames);
    }

    private static string InferDisplayNameFromPath(string path)
    {
        try
        {
            return Path.GetFileName(path.TrimEnd('\\', '/')) ?? path;
        }
        catch
        {
            return path;
        }
    }
}
