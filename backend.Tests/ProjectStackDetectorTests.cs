using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectStackDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "project-stack-detector-" + Guid.NewGuid().ToString("N"));

    public ProjectStackDetectorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "ProjectStackDetectorTests cleanup"); }
    }

    [Fact]
    public void Detect_AngularMarkerDerivesAngularAndPackageDerivesNode()
    {
        Write("frontend/angular.json", "{}");
        Write("frontend/package.json", "{}");

        var stacks = ProjectStackDetector.Detect(_root);

        Assert.Equal([PipelineStepStacks.Angular, PipelineStepStacks.Node], stacks);
    }

    [Theory]
    [InlineData("App.slnx")]
    [InlineData("src/App/App.csproj")]
    public void Detect_SolutionOrProjectDerivesDotNet(string marker)
    {
        Write(marker, "<Project />");

        Assert.Equal([PipelineStepStacks.DotNet], ProjectStackDetector.Detect(_root));
    }

    [Fact]
    public void Detect_MixedRepositoryReturnsEveryConventionWithoutSettings()
    {
        Write("angular.json", "{}");
        Write("package.json", "{}");
        Write("backend/App.csproj", "<Project />");

        Assert.Equal(
            [PipelineStepStacks.Angular, PipelineStepStacks.DotNet, PipelineStepStacks.Node],
            ProjectStackDetector.Detect(_root));
    }

    [Fact]
    public void Applies_AngularStepIsUnavailableForPureDotNetProject()
    {
        Write("App.slnx", "<Solution />");
        var stacks = ProjectStackDetector.Detect(_root);

        Assert.False(ProjectStackDetector.Applies(PipelineStepStacks.Angular, stacks));
        Assert.True(ProjectStackDetector.Applies(PipelineStepStacks.Any, stacks));
    }

    [Fact]
    public void EffectiveExecution_UsesBuildProfileCommandsAndResolvedStylelintWorkspace()
    {
        Write("frontend/angular.json", "{}");
        var settings = new ProjectSettings
        {
            BuildProfile = new BuildProfile
            {
                BuildCmds = ["dotnet build src/App.csproj"],
                TestCmds = ["dotnet test tests/App.Tests.csproj"],
            },
        };
        var gate = PipelineCatalogue.Standard.Post.Single(step =>
            step.Id == PipelineCatalogue.BuildTestGateStepId);
        var lint = PipelineCatalogue.Standard.Post.Single(step =>
            step.Id == PipelineCatalogue.LintScssStepId);

        var gateExecution = PipelineStepExecutionResolver.Resolve(gate, _root, settings);
        var lintExecution = PipelineStepExecutionResolver.Resolve(lint, _root, settings);

        Assert.Equal(VerifyPlan.SourceBuildProfile, gateExecution.Source);
        Assert.Equal(
            ["dotnet build src/App.csproj", "dotnet test tests/App.Tests.csproj"],
            gateExecution.Commands.Select(command => command.Command));
        var stylelint = Assert.Single(lintExecution.Commands);
        Assert.Equal("frontend", stylelint.WorkingSubdir);
        Assert.Equal(FrontendStylelintCommand.Command, stylelint.Command);
    }

    [Fact]
    public async Task Probe_DelegatesShellExecutionToBuildTestGateRunner()
    {
        Write("angular.json", "{}");
        var fakeGate = new FakeBuildTestGateRunner();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var service = new PipelineStepProbeService(fakeGate, settings, config);
        var step = PipelineCatalogue.Standard.Post.Single(candidate =>
            candidate.Id == PipelineCatalogue.LintScssStepId);

        var result = await service.RunAsync("Angular", _root, step, CancellationToken.None);

        Assert.Equal("passed", result.Status);
        Assert.Equal(PipelineCatalogue.LintScssStepId, fakeGate.Request?.GateId);
        Assert.False(fakeGate.Request!.RequireExactSubject);
        Assert.Contains(FrontendStylelintCommand.Command, fakeGate.Profile?.BuildCmds ?? []);
    }

    [Fact]
    public async Task Probe_InternalStepFromNonStandardPipelineReturnsDiagnosticOutput()
    {
        var fakeGate = new FakeBuildTestGateRunner();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var service = new PipelineStepProbeService(fakeGate, settings, config);
        var step = Assert.IsType<PipelineStep>(
            PipelineCatalogue.FindStep(PipelineCatalogue.UiHumanReviewGateStepId));

        var result = await service.RunAsync("UI Project", _root, step, CancellationToken.None);

        Assert.Equal("unavailable", result.Status);
        Assert.Contains("task/run context", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fakeGate.InvocationCount);
    }

    [Fact]
    public async Task StylelintStep_IsProjectSpecific_AndDoesNotProbePureDotNetRepository()
    {
        var angularRoot = Path.Combine(_root, "angular-app");
        var dotNetRoot = Path.Combine(_root, "dotnet-app");
        Write("angular-app/angular.json", "{}");
        Write("angular-app/package.json", "{}");
        Write("dotnet-app/App.csproj", "<Project />");
        var step = PipelineCatalogue.Standard.Post.Single(candidate =>
            candidate.Id == PipelineCatalogue.LintScssStepId);

        var angularStacks = ProjectStackDetector.Detect(angularRoot);
        var dotNetStacks = ProjectStackDetector.Detect(dotNetRoot);

        Assert.Equal(PipelineStepStacks.Angular, step.AppliesTo);
        Assert.Equal([PipelineStepStacks.Angular, PipelineStepStacks.Node], angularStacks);
        Assert.Equal([PipelineStepStacks.DotNet], dotNetStacks);
        Assert.True(ProjectStackDetector.Applies(step.AppliesTo, angularStacks));
        Assert.False(ProjectStackDetector.Applies(step.AppliesTo, dotNetStacks));

        var execution = PipelineStepExecutionResolver.Resolve(step, angularRoot, settings: null);
        var command = Assert.Single(execution.Commands);
        Assert.Equal("", command.WorkingSubdir);
        Assert.Equal(FrontendStylelintCommand.Command, command.Command);

        var fakeGate = new FakeBuildTestGateRunner();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var probes = new PipelineStepProbeService(fakeGate, settings, config);

        var angularProbe = await probes.RunAsync("Angular", angularRoot, step, CancellationToken.None);
        var dotNetProbe = await probes.RunAsync(".NET", dotNetRoot, step, CancellationToken.None);

        Assert.Equal("passed", angularProbe.Status);
        Assert.Equal("not-applicable", dotNetProbe.Status);
        Assert.False(dotNetProbe.Applicable);
        Assert.Contains("requires angular", dotNetProbe.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fakeGate.InvocationCount);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed class FakeBuildTestGateRunner : IBuildTestGateRunner
    {
        public BuildTestGateRequest? Request { get; private set; }
        public BuildProfile? Profile { get; private set; }
        public int InvocationCount { get; private set; }

        public Task<BuildTestGateResult> RunAsync(
            BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles,
            BuildProfile? profile,
            PostStepMode mode,
            TimeSpan timeout,
            CancellationToken ct)
        {
            InvocationCount++;
            Request = request;
            Profile = profile;
            return Task.FromResult(new BuildTestGateResult(
                BuildTestGateVerdict.Ok, 0, 12, "probe output", "passed", false, true));
        }
    }
}
