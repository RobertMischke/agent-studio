using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteReviewPlanQualityAnalysisTests
{
    [Fact]
    public void FrontendCard_FreezesInProcessAngularRulePassAtResultSubject()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PromptTemplates:RuntimePath"] = Path.Combine(
                    Directory.GetCurrentDirectory(), "prompts", "runtime"),
            })
            .Build();
        var prompts = new RuntimePromptService(
            configuration,
            NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(
            prompts,
            NullLogger<AspectRunnerService>.Instance);
        var builder = new RemoteReviewPlanBuilder(aspects, configuration);
        var task = new TaskInfo
        {
            Id = "frontend-card",
            Title = "Frontend card",
            ProjectName = "Agent Studio",
            Commit = new TaskCommitInfo
            {
                Sha = new string('a', 40),
                Files =
                [
                    "frontend/src/app/card.component.ts",
                    "frontend/src/app/card.component.html",
                    "docs/note.md",
                ],
            },
        };

        var plan = builder.Build(task, repositoryPath: null, projectSettings: null, "refs/heads/develop");

        var command = Assert.Single(
            plan.Commands,
            candidate => candidate.ExecutionKind == ReviewCommandKinds.QualityAnalysis);
        Assert.Equal(PipelineCatalogue.QualityStaticRulesStepId, command.StepId);
        Assert.Equal(QualityAnalysisPolicy.AngularRuleAxis, command.Aspect);
        Assert.Equal(QualityAnalysisPolicy.AngularRuleAnalysis, command.FileName);
        Assert.Equal(
            ["frontend/src/app/card.component.ts", "frontend/src/app/card.component.html"],
            command.Arguments);
    }
}
