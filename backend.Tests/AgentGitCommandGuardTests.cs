using System.Diagnostics;
using OrchestratorApi.Services.Cli;
using Xunit;

namespace OrchestratorApi.Tests;

public class AgentGitCommandGuardTests
{
    [Theory]
    [InlineData("commit")]
    [InlineData("push")]
    [InlineData("commit-tree")]
    [InlineData("reset")]
    [InlineData("checkout")]
    [InlineData("switch")]
    [InlineData("branch")]
    public void IsForbiddenGitCommand_BlocksMutatingCommands(string command)
    {
        Assert.True(AgentGitCommandGuard.IsForbiddenGitCommand([command]));
        Assert.True(AgentGitCommandGuard.IsForbiddenGitCommand(["-C", "repo", command]));
        Assert.True(AgentGitCommandGuard.IsForbiddenGitCommand(["-c", "user.name=agent", command]));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("diff")]
    [InlineData("log")]
    [InlineData("rev-parse")]
    public void IsForbiddenGitCommand_AllowsReadOnlyInspection(string command)
    {
        Assert.False(AgentGitCommandGuard.IsForbiddenGitCommand([command]));
        Assert.False(AgentGitCommandGuard.IsForbiddenGitCommand(["-C", "repo", command]));
    }

    [Fact]
    public void Apply_PrependsGuardPathAndCapturesRealGit()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "agent",
            UseShellExecute = false
        };

        AgentGitCommandGuard.Apply(psi);

        Assert.True(psi.Environment.ContainsKey(AgentGitCommandGuard.GuardDirEnv));
        Assert.True(psi.Environment.ContainsKey(AgentGitCommandGuard.RealGitEnv));
        Assert.True(psi.Environment.ContainsKey("PATH"));
        Assert.StartsWith(
            psi.Environment[AgentGitCommandGuard.GuardDirEnv] + Path.PathSeparator,
            psi.Environment["PATH"]);
    }

    [Fact]
    public void Apply_ExplicitAllowFlagLeavesPathUnchanged()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "agent",
            UseShellExecute = false
        };
        psi.Environment[AgentGitCommandGuard.AllowEnv] = "1";
        var before = psi.Environment["PATH"];

        AgentGitCommandGuard.Apply(psi);

        Assert.Equal(before, psi.Environment["PATH"]);
        Assert.False(psi.Environment.ContainsKey(AgentGitCommandGuard.GuardDirEnv));
    }
}
