namespace AgentStudio.Projects;

/// <summary>
/// Projects assigned to a remote host need a durable repository URL before
/// that host can claim any of their cards. The policy is intentionally pure so
/// settings, host status, and claim selection render the same invariant.
/// </summary>
public static class RemoteProjectClaimabilityPolicy
{
    public const string MissingRepositoryDetail =
        "Remote execution is not claimable: repositoryUrl is missing; repository URL is not configured.";

    public static bool RequiresRepositoryUrl(ProjectSettings settings) =>
        ProjectExecutionPolicy.ResolveExecutionLocation(settings) != ExecutionLocations.Local;

    public static bool IsMissingRepositoryUrl(ProjectRecord project, ProjectSettings settings) =>
        RequiresRepositoryUrl(settings)
        && RemoteProjectRepositoryResolver.Resolve(project, settings.IntegrationBranch) is null;

    public static IReadOnlyList<RunnerProjectPreflight> ProjectForRunner(
        ClientIdentity runner,
        IEnumerable<ProjectRecord> projects,
        ProjectSettingsService settings,
        DateTime now)
    {
        var projected = runner.RunnerProjectPreflights.ToDictionary(
            item => item.ProjectId,
            StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            var projectSettings = settings.Get(project.DisplayName);
            if (!ProjectExecutionPolicy.IsAssignedRemote(
                    projectSettings,
                    runner.Id,
                    runner.DisplayName))
                continue;

            if (!IsMissingRepositoryUrl(project, projectSettings))
                continue;

            projected[project.Id] = new RunnerProjectPreflight
            {
                ProjectId = project.Id,
                ProjectName = project.DisplayName,
                RegistrationFingerprint = ProjectDeliveryPreflightFingerprint.CreateUnconfigured(
                    project.Id,
                    projectSettings.IntegrationBranch),
                TargetBranch = projectSettings.IntegrationBranch,
                Status = "failed",
                Detail = MissingRepositoryDetail,
                CheckedAt = now,
            };
        }

        return [.. projected.Values.OrderBy(item => item.ProjectName, StringComparer.OrdinalIgnoreCase)];
    }
}
