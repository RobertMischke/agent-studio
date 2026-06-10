using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using AgentStudio.Cli;

namespace AgentStudio.Tests;

/// <summary>
/// T1b / ASS-1742: the context-mode vocabulary (<see cref="CliContextModes"/>)
/// and the per-run clean-context builder (<see cref="CleanContextPreparer"/>).
/// "clean" is not a CLI flag — each adapter relocates the CLI's whole config
/// home to a seeded per-run temp dir. These tests lock the two invariants the
/// rest of the feature leans on: (1) the vocabulary defaults to CLEAN and only
/// Claude/Codex report clean support; (2) the preparer creates an isolated home,
/// seeds only the auth + base config, points the right env var at it, surfaces
/// the temp paths as sources (for the T1a panel), and tears the home down on
/// dispose.
/// </summary>
public sealed class CliContextModesTests : IDisposable
{
    private readonly List<string> _homes = new();

    public void Dispose()
    {
        foreach (var h in _homes)
            try { if (Directory.Exists(h)) Directory.Delete(h, recursive: true); } catch { /* best-effort */ }
    }

    // --- vocabulary -------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    public void Normalize_UnknownOrEmpty_DefaultsToClean(string? input)
        => Assert.Equal(CliContextModes.Clean, CliContextModes.Normalize(input));

    [Theory]
    [InlineData("clean", "clean")]
    [InlineData("CLEAN", "clean")]
    [InlineData("shared", "shared")]
    [InlineData("Shared", "shared")]
    public void Normalize_KnownValues_CanonicalizeCaseInsensitively(string input, string expected)
        => Assert.Equal(expected, CliContextModes.Normalize(input));

    [Fact]
    public void IsValid_OnlyAcceptsKnownModes()
    {
        Assert.True(CliContextModes.IsValid("clean"));
        Assert.True(CliContextModes.IsValid("shared"));
        Assert.False(CliContextModes.IsValid("yolo"));
        Assert.False(CliContextModes.IsValid(null));
    }

    [Fact]
    public void SupportsClean_OnlyClaudeAndCodex()
    {
        Assert.True(CliContextModes.SupportsClean(CliTypes.Claude));
        Assert.True(CliContextModes.SupportsClean(CliTypes.Codex));
        Assert.False(CliContextModes.SupportsClean(CliTypes.Copilot));
        Assert.False(CliContextModes.SupportsClean(CliTypes.Gemini));
    }

    // --- CleanContextPreparer (Claude) -----------------------------------

    [Fact]
    public void PrepareClaude_CreatesIsolatedHome_SeedsAuthAndSettings_PointsConfigDir()
    {
        var userHome = NewUserHome();
        WriteFile(Path.Combine(userHome, ".claude", ".credentials.json"), "{\"token\":\"x\"}");
        WriteFile(Path.Combine(userHome, ".claude", "settings.json"), "{}");
        // Deliberately-excluded state: must NOT be carried into the clean home.
        WriteFile(Path.Combine(userHome, ".claude", "CLAUDE.md"), "user memory");
        WriteFile(Path.Combine(userHome, ".claude", "projects", "p", "session.jsonl"), "history");

        using var prep = CleanContextPreparer.PrepareClaude(userHome, NullLogger.Instance);

        Assert.NotNull(prep);
        _homes.Add(prep!.TempHome);
        Assert.Equal(CliTypes.Claude, prep.CliType);
        Assert.True(Directory.Exists(prep.TempHome));
        // The env override points the CLI's config-dir at the temp home.
        Assert.True(prep.EnvOverrides.TryGetValue("CLAUDE_CONFIG_DIR", out var dir));
        Assert.Equal(prep.TempHome, dir);
        // Only the allow-listed files were seeded.
        Assert.True(File.Exists(Path.Combine(prep.TempHome, ".credentials.json")));
        Assert.True(File.Exists(Path.Combine(prep.TempHome, "settings.json")));
        Assert.False(File.Exists(Path.Combine(prep.TempHome, "CLAUDE.md")));
        Assert.False(Directory.Exists(Path.Combine(prep.TempHome, "projects")));
        // The temp home is surfaced as an Env source (the T1a panel shows it).
        Assert.Contains(prep.Sources, s =>
            s.Kind == CliContextSourceKinds.Env && s.Path == prep.TempHome);
        // Seeded files are surfaced as global-config sources.
        Assert.Contains(prep.Sources, s =>
            s.Kind == CliContextSourceKinds.GlobalConfig && (s.Label?.Contains(".credentials.json") ?? false));
    }

    [Fact]
    public void PrepareCodex_PointsCodexHome_AndSeedsAuthAndConfig()
    {
        var userHome = NewUserHome();
        WriteFile(Path.Combine(userHome, ".codex", "auth.json"), "{\"key\":\"x\"}");
        WriteFile(Path.Combine(userHome, ".codex", "config.toml"), "model = \"gpt-5-codex\"");
        WriteFile(Path.Combine(userHome, ".codex", "history.jsonl"), "history");

        using var prep = CleanContextPreparer.PrepareCodex(userHome, NullLogger.Instance);

        Assert.NotNull(prep);
        _homes.Add(prep!.TempHome);
        Assert.Equal(CliTypes.Codex, prep.CliType);
        Assert.True(prep.EnvOverrides.TryGetValue("CODEX_HOME", out var dir));
        Assert.Equal(prep.TempHome, dir);
        Assert.True(File.Exists(Path.Combine(prep.TempHome, "auth.json")));
        Assert.True(File.Exists(Path.Combine(prep.TempHome, "config.toml")));
        Assert.False(File.Exists(Path.Combine(prep.TempHome, "history.jsonl")));
    }

    [Fact]
    public void Prepare_MissingSourceFiles_StillCreatesHome_AuthMayComeFromEnv()
    {
        // No ~/.claude at all: auth may be supplied via ANTHROPIC_API_KEY, so a
        // missing seed is non-fatal and the clean home is still created.
        var userHome = NewUserHome();

        using var prep = CleanContextPreparer.PrepareClaude(userHome, NullLogger.Instance);

        Assert.NotNull(prep);
        _homes.Add(prep!.TempHome);
        Assert.True(Directory.Exists(prep.TempHome));
        Assert.False(File.Exists(Path.Combine(prep.TempHome, ".credentials.json")));
        // The env override is present regardless so the CLI reads the isolated home.
        Assert.True(prep.EnvOverrides.ContainsKey("CLAUDE_CONFIG_DIR"));
    }

    [Fact]
    public void Dispose_TearsDownTheTempHome()
    {
        var userHome = NewUserHome();
        WriteFile(Path.Combine(userHome, ".claude", ".credentials.json"), "{}");
        var prep = CleanContextPreparer.PrepareClaude(userHome, NullLogger.Instance);
        Assert.NotNull(prep);
        var home = prep!.TempHome;
        Assert.True(Directory.Exists(home));

        prep.Dispose();

        Assert.False(Directory.Exists(home));
        // Idempotent: a second dispose is a no-op, never throws.
        prep.Dispose();
    }

    private string NewUserHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "atp-clean-ctx-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        _homes.Add(home);
        return home;
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
