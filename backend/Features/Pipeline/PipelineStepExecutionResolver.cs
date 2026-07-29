namespace AgentStudio.Pipeline;

public sealed record EffectivePipelineCommand(string WorkingSubdir, string Command);

public sealed record EffectivePipelineStepExecution(
    string ExecutionKind,
    string Source,
    IReadOnlyList<EffectivePipelineCommand> Commands);

/// <summary>
/// Resolves the project-level command view from the same inputs as the runtime.
/// Commands are fully expanded shell strings; no UI placeholder rendering is
/// involved.
/// </summary>
public static class PipelineStepExecutionResolver
{
    public static EffectivePipelineStepExecution Resolve(
        PipelineStep step,
        string repositoryPath,
        ProjectSettings? settings)
    {
        if (step.Id.Equals(PipelineCatalogue.BuildTestGateStepId, StringComparison.OrdinalIgnoreCase))
        {
            var plan = VerifyCommandPlanner.Plan(repositoryPath, settings?.BuildProfile);
            return new EffectivePipelineStepExecution(
                "shell",
                plan.Source,
                plan.Commands.Select(command =>
                    new EffectivePipelineCommand(command.WorkingSubdir, command.Command)).ToArray());
        }

        if (step.Id.Equals(PipelineCatalogue.LintScssStepId, StringComparison.OrdinalIgnoreCase))
        {
            var workspace = FrontendStylelintCommand.ResolveWorkspace(repositoryPath);
            if (workspace is null)
                return new EffectivePipelineStepExecution("shell", "catalogue", []);
            var relative = Path.GetRelativePath(Path.GetFullPath(repositoryPath), workspace);
            if (relative == ".") relative = "";
            return new EffectivePipelineStepExecution(
                "shell", "catalogue",
                [new EffectivePipelineCommand(relative.Replace('\\', '/'), FrontendStylelintCommand.Command)]);
        }

        return new EffectivePipelineStepExecution("internal", "runtime", []);
    }
}

/// <summary>One source of truth for the stylelint command and its workspace.</summary>
public static class FrontendStylelintCommand
{
    public const string Command = "npx stylelint \"src/**/*.scss\"";

    public static string? ResolveWorkspace(string repositoryPath)
        => ProjectStackDetector.FindDirectoryContaining(repositoryPath, "angular.json");
}
