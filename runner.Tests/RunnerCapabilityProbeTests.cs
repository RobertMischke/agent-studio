using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerCapabilityProbeTests
{
    [Theory]
    [InlineData(1, "", "HTTP 401 Missing bearer authentication", true)]
    [InlineData(1, "", "login required", true)]
    [InlineData(1, "", "ordinary product failure", false)]
    [InlineData(0, "", "HTTP 401 in historical output", false)]
    public void Provider_authentication_failure_requires_a_nonzero_typed_signal(
        int exitCode,
        string stdout,
        string stderr,
        bool expected)
        => Assert.Equal(
            expected,
            RunnerCapabilityProbe.IsProviderAuthenticationFailure(
                new ProcessResult(exitCode, stdout, stderr)));

    [Theory]
    [InlineData("/usr/local/bin/codex", "codex")]
    [InlineData("claude.exe", "claude")]
    public void Provider_identity_is_stable_across_binary_paths(string binary, string expected)
        => Assert.Equal(expected, RunnerCapabilityProbe.Provider(binary));

    [Theory]
    [InlineData(
        "remote: refusing to allow a Personal Access Token to create or update workflow `.github/workflows/release.yml` without `workflow` scope",
        true)]
    [InlineData(
        "remote: GitHub App is not permitted to update workflow file .github/workflows/release.yml without Workflows permission",
        true)]
    [InlineData("remote: permission denied to refs/heads/runner/test", false)]
    [InlineData("fatal: Authentication failed for https://github.com/example/repo.git", false)]
    public void Workflow_scope_failure_requires_a_workflow_specific_rejection(
        string error,
        bool expected)
        => Assert.Equal(expected, GitPushProbe.IsWorkflowScopeFailure(error));

    [Fact]
    public void Workflow_scope_failure_ignores_successful_historical_output()
        => Assert.False(GitPushProbe.IsWorkflowScopeFailure(new ProcessResult(
            0,
            "previous warning mentioned .github/workflows and workflow scope",
            "")));

    [Fact]
    public void Capability_advertisement_reports_workflow_scope_without_making_it_a_claim_requirement()
    {
        var options = new RunnerOptions
        {
            ServerUrl = "http://task-server",
            RunnerId = "runner-test",
            RunnerName = "runner-test",
            Hostname = "test-host",
            BackendName = "test",
            GitRemote = "https://github.com/example/repo.git",
            WorkDir = Path.GetTempPath(),
            BaseBranch = "main",
            CliBin = "codex",
            CliArgs = "",
        };

        var advertised = RunnerCapabilityProbe.Advertise(
            options,
            gitPushReady: true,
            gitWorkflowPushReady: false,
            gitDetail: "workflow scope missing");

        Assert.Equal(
            "ready",
            Assert.Single(advertised, item => item.Key == CapabilityProtocol.GitPush).Status);
        var workflow = Assert.Single(
            advertised,
            item => item.Key == CapabilityProtocol.GitWorkflowPush);
        Assert.Equal(GitPushProbe.ReadyNoWorkflowScope, workflow.Status);
        Assert.Equal("workflow scope missing", workflow.Detail);
        Assert.DoesNotContain(
            CapabilityProtocol.GitWorkflowPush,
            RunnerCapabilityProbe.CodingRequirements(options));
    }

    [Fact]
    public void Salvage_gate_adds_the_token_scope_fix_for_a_real_workflow_rejection()
    {
        var rejection = new InvalidOperationException(
            "remote: refusing to allow a Personal Access Token to update workflow " +
            ".github/workflows/release.yml without workflow scope");
        var exception = new WorktreeSalvageException(
            "/runner/worktrees/AGT-2347",
            "runner/host/AGT-2347",
            rejection,
            localCommitSha: "abc123",
            remoteCommitSha: "def456");

        var gate = RemoteTaskRunner.BuildUnsecuredWorktreeGate("runner-host", exception);

        Assert.Contains("host=runner-host", gate);
        Assert.Contains("worktree=/runner/worktrees/AGT-2347", gate);
        Assert.Contains("branch=runner/host/AGT-2347", gate);
        Assert.Contains("fine-grained Contents: Read and write", gate);
        Assert.Contains("classic repo plus workflow", gate);
        Assert.Contains(GitPushProbe.TokenRequirementsPath, gate);
    }
}
