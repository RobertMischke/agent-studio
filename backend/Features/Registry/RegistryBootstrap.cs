

namespace AgentStudio.Registry;

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
        TaskScannerService scanner,
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

        // Initial shared-component declaration. It is seeded into the CAC
        // project metadata once both projects are known, then behaves exactly
        // like an operator-created Project Hub mapping. Subsequent boots do
        // not overwrite owner edits.
        var cac = projects.FindByShortCode("CAC");
        var agt = projects.FindByShortCode("AGT");
        if (cac != null && agt != null
            && !cac.OwnershipMappings.Any(row => string.Equals(row.Id, "cac-agent-studio-chat", StringComparison.OrdinalIgnoreCase)))
        {
            projects.UpsertOwnershipMapping(cac.Id, new ComponentOwnershipMapping
            {
                Id = "cac-agent-studio-chat",
                ObservedSurfaces = ["Agent Studio Orchestrator chat", "Agent Studio chat rendering", "chat footer", "chat message"],
                Component = "Coding Agent Chat rendering, footer, and message components",
                PackageOrModule = "coding-agent-chat",
                Repository = "coding-agent-chat",
                PrimaryProjectId = cac.Id,
                ConsumerProjectIds = [agt.Id],
                IntegrationHosts = ["Agent Studio"],
                ReleaseArtifact = "coding-agent-chat npm package",
                VersioningMechanism = "npm package version",
                DeploymentSteps = ["Publish coding-agent-chat package", "Update Agent Studio dependency", "Build and deploy Agent Studio"],
                Environments = ["development", "stable"],
                AllowedTicketPrefix = cac.ShortCode,
                Evidence = ["frontend/AGENTS.md chat surfaces contract", "Agent Studio package dependency"],
                Confidence = 1,
            }, "registry-bootstrap");
        }
        if (agt != null
            && !agt.OwnershipMappings.Any(row => string.Equals(row.Id, "agent-studio-backend", StringComparison.OrdinalIgnoreCase)))
        {
            projects.UpsertOwnershipMapping(agt.Id, new ComponentOwnershipMapping
            {
                Id = "agent-studio-backend",
                ObservedSurfaces = ["Agent Studio backend", "Agent Studio API", "Agent Studio orchestrator backend"],
                Component = "Agent Studio backend and API",
                PackageOrModule = "backend",
                Repository = "agent-taskboard",
                PrimaryProjectId = agt.Id,
                AllowedTicketPrefix = agt.ShortCode,
                Evidence = ["Agent Studio backend source and deployment ownership"],
                Confidence = 1,
            }, "registry-bootstrap");
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
