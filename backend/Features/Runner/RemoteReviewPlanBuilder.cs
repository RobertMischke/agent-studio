using AgentStudio.Pipeline;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Adds remote-capable semantic aspect work to the deterministic Review Plane
/// plan. The Task Server still owns admission and the immutable subject; the
/// Runner Host owns CLI and repository execution after a fenced claim.
/// </summary>
internal static class RemoteReviewPlanBuilder
{
    internal const string SemanticAspectExecutionKind = "semantic-aspect";

    public static Contract.ReviewPlanDto Build(
        Contract.ReviewPlanDto tools,
        TaskInfo? task,
        AgentStudio.Projects.ProjectSettingsService settingsService,
        AspectRunnerService aspectRunner,
        string? integrationRef)
    {
        if (task is null) return tools;

        var settings = PipelineTypeSettings.ForTask(settingsService.Get(task.ProjectName), task);
        var pipeline = ProjectPipelineOrder.Apply(PipelineCatalogue.ForTask(task), settings);
        var condition = new PipelineStepConditionContext
        {
            Aborted = false,
            ExitCode = 0,
            AnyAspectFailed = false,
            TaskType = task.TaskType,
            Tags = task.Tags,
        };
        var inputs = Inputs(task, integrationRef);
        var commands = tools.Commands.ToList();

        foreach (var step in pipeline.Post.Where(step =>
                     step.Kind == StepKind.Aspect
                     && PipelineStepConfigResolver.ShouldRun(settings, step, condition)))
        {
            var aspectId = step.Id.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase)
                ? step.Id["aspect-".Length..]
                : step.Id;
            if (!AspectRunnerService.Catalogue.TryGetValue(aspectId, out var definition))
                continue;

            var modelResolution = PipelineStepModelDefaults.Resolve(settings, step);
            var model = modelResolution?.Model ?? PipelineStepModelDefaults.SupportModel;
            var cli = PipelineStepConfigResolver.ResolveCliType(settings, step)
                      ?? PipelineStepModelDefaults.RuntimeDefaultCliFor(step)
                      ?? PipelineStepModelDefaults.DefaultCli;
            var thinking = PipelineStepConfigResolver.ResolveThinkingLevel(
                settings,
                step,
                cli,
                model,
                PipelineStepModelDefaults.RuntimeDefaultThinkingLevelFor(step));
            var prompt = aspectRunner.RenderPrompt(
                definition,
                inputs,
                model,
                PipelineStepConfigResolver.ResolvePrompt(settings, step));

            commands.Add(new Contract.ReviewCommandDto(
                step.Id,
                aspectId,
                "agent-cli",
                [],
                TimeoutSeconds: 1800,
                ExecutionKind: SemanticAspectExecutionKind,
                CliType: cli,
                Model: model,
                ThinkingLevel: thinking,
                Prompt: prompt,
                PipelineStepId: step.Id,
                PipelineStepClass: "aspect"));
        }

        return tools with
        {
            Commands = commands,
            RequiredAspects = commands
                .Where(command => command.Required)
                .Select(command => command.Aspect)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
    }

    private static AspectRunInputs Inputs(TaskInfo task, string? integrationRef)
    {
        var taskBody = ReadBounded(Path.Combine(task.FolderPath, "prompt.md"), 64_000);
        var recentLog = ReadTail(TaskPaths.CliOutputLog(task.FolderPath), 24_000);
        var status = ReadBounded(Path.Combine(task.FolderPath, "status.md"), 16_000);
        var baseline = string.IsNullOrWhiteSpace(integrationRef) ? "the integration ref" : integrationRef;
        return new AspectRunInputs(
            task.ProjectName,
            task.Id,
            task.Title ?? task.Id,
            task.FolderPath,
            taskBody,
            recentLog,
            $"The immutable Result-SHA is the current Runner workspace. Inspect git diff {baseline}...HEAD and the changed files directly before deciding.",
            status)
        {
            ResultsInventory = ResultsInventory.Render(task.FolderPath),
            CardMode = ReviewCardMode.Describe(task.Mode),
        };
    }

    private static string ReadBounded(string path, int maxChars)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            var text = File.ReadAllText(path);
            return text.Length <= maxChars ? text : text[..maxChars];
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadTail(string path, int maxChars)
    {
        var text = ReadBounded(path, int.MaxValue);
        return text.Length <= maxChars ? text : text[^maxChars..];
    }
}
