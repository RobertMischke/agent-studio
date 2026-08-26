using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalCliSelfHealTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-cli-self-heal-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("claude", "@anthropic-ai", "claude-code", "@anthropic-ai/claude-code", "2.1.234")]
    [InlineData("codex", "@openai", "codex", "@openai/codex", "1.2.3")]
    public void Package_present_without_command_shim_is_repairable(
        string cliType,
        string scope,
        string packageFolder,
        string packageName,
        string version)
    {
        var packagePath = Path.Combine(_root, "node_modules", scope, packageFolder);
        Directory.CreateDirectory(packagePath);
        File.WriteAllText(
            Path.Combine(packagePath, "package.json"),
            $$"""{ "name": "{{packageName}}", "version": "{{version}}" }""");

        var inspection = NpmGlobalCliPackageInspector.Inspect(cliType, _root);

        Assert.True(inspection.PackagePresent);
        Assert.True(inspection.MissingShimWithPackagePresent);
        Assert.Equal(packageName, inspection.PackageName);
        Assert.Equal(version, inspection.PackageVersion);
        Assert.False(inspection.CommandShimPresent);
    }

    [Fact]
    public void Missing_package_is_truly_uninstalled_and_not_repairable()
    {
        Directory.CreateDirectory(_root);

        var inspection = NpmGlobalCliPackageInspector.Inspect("claude", _root);

        Assert.False(inspection.PackagePresent);
        Assert.False(inspection.MissingShimWithPackagePresent);
        Assert.Null(inspection.PackageVersion);
    }

    [Fact]
    public void Existing_windows_command_shim_is_not_the_missing_shim_shape()
    {
        var packagePath = Path.Combine(_root, "node_modules", "@anthropic-ai", "claude-code");
        Directory.CreateDirectory(packagePath);
        File.WriteAllText(Path.Combine(packagePath, "package.json"), """{ "version": "2.1.234" }""");
        File.WriteAllText(Path.Combine(_root, "claude.cmd"), "@echo off");

        var inspection = NpmGlobalCliPackageInspector.Inspect("claude", _root);

        Assert.True(inspection.CommandShimPresent);
        Assert.False(inspection.MissingShimWithPackagePresent);
    }

    [Fact]
    public void Bash_shim_without_windows_command_shim_remains_repairable()
    {
        var packagePath = Path.Combine(_root, "node_modules", "@anthropic-ai", "claude-code");
        Directory.CreateDirectory(packagePath);
        File.WriteAllText(Path.Combine(packagePath, "package.json"), """{ "version": "2.1.234" }""");
        File.WriteAllText(Path.Combine(_root, "claude"), "#!/usr/bin/env sh");

        var inspection = NpmGlobalCliPackageInspector.Inspect("claude", _root);

        Assert.True(inspection.ShellShimPresent);
        Assert.False(inspection.CommandShimPresent);
        Assert.True(inspection.MissingShimWithPackagePresent);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
