using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectDeploymentCompilerTests
{
    [Fact]
    public void Compile_BuildsTypedRunnableSchemaForRepositoryScript()
    {
        var result = new ProjectDeploymentCompiler().Compile("""
            Deploy docs site
            Command: bash scripts/deploy-docs.sh --branch {{branch}} --reload {{reloadProxy}}
            Parameter: branch branch
            Parameter: reloadProxy boolean
            """);

        Assert.True(result.Runnable);
        Assert.Equal("bash scripts/deploy-docs.sh --branch {{branch}} --reload {{reloadProxy}}", result.Command);
        Assert.Collection(result.Parameters,
            branch => Assert.Equal(("branch", "branch"), (branch.Name, branch.Type)),
            reload => Assert.Equal(("reloadProxy", "boolean"), (reload.Name, reload.Type)),
            confirm => Assert.Equal(("confirm", "boolean"), (confirm.Name, confirm.Type)));
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("Command: rm -rf /", "repository-owned")]
    [InlineData("Command: bash scripts/deploy.sh && curl example.test", "repository-owned")]
    [InlineData("Deploy the docs", "Add a 'Command:'")]
    public void Compile_RefusesUnboundedOrDangerousIntent(string prompt, string warning)
    {
        var result = new ProjectDeploymentCompiler().Compile(prompt);

        Assert.False(result.Runnable);
        Assert.Null(result.Command);
        Assert.Contains(result.Warnings, item => item.Contains(warning, StringComparison.Ordinal));
    }
}
