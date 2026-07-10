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
/// dispose. AGT-2066 adds a third: the credential file is <b>shared by link</b>
/// (not copied) so a mid-run OAuth refresh persists into the one home file every
/// later launch reads, while base config stays an isolated copy.
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

    // --- AGT-2066: credential is shared by link, not copied ---------------

    [Fact]
    public void PrepareClaude_CredentialsSharedByLink_RefreshWritesThroughToHome()
    {
        // The clean home's .credentials.json must be a LINK to the one home file,
        // not a throwaway copy: when the CLI rotates the OAuth token mid-run it
        // rewrites the file in its config dir, and that new token has to land in
        // the single home file every later launch reads (incident 2026-07-10).
        var userHome = NewUserHome();
        var homeCred = Path.Combine(userHome, ".claude", ".credentials.json");
        WriteFile(homeCred, "{\"token\":\"old\"}");
        WriteFile(Path.Combine(userHome, ".claude", "settings.json"), "{}");

        using var prep = CleanContextPreparer.PrepareClaude(userHome, NullLogger.Instance);
        Assert.NotNull(prep);
        _homes.Add(prep!.TempHome);

        var tempCred = Path.Combine(prep.TempHome, ".credentials.json");
        Assert.True(File.Exists(tempCred));

        // Simulate the CLI's in-place refresh write against the config-dir file.
        File.WriteAllText(tempCred, "{\"token\":\"refreshed\"}");

        // It wrote through to the shared home file.
        Assert.Equal("{\"token\":\"refreshed\"}", File.ReadAllText(homeCred));

        // The temp home is surfaced as a Linked source (T1a panel truthfulness).
        Assert.Contains(prep.Sources, s =>
            s.Kind == CliContextSourceKinds.GlobalConfig
            && (s.Label?.Contains(".credentials.json") ?? false)
            && (s.Label?.StartsWith("Linked") ?? false));
    }

    [Fact]
    public void PrepareClaude_ParallelRuns_ShareOneCredential_SoWinningRefreshSurvives()
    {
        // The reproduction: many parallel clean contexts off the one home. Under
        // the old copy behaviour a token rotation in one run died with its temp
        // dir; with a shared link the winning refresh persists for everyone.
        var userHome = NewUserHome();
        var homeCred = Path.Combine(userHome, ".claude", ".credentials.json");
        WriteFile(homeCred, "{\"token\":\"expired\"}");

        var preps = new List<CleanContextPreparation>();
        for (var i = 0; i < 12; i++)
        {
            var p = CleanContextPreparer.PrepareClaude(userHome, NullLogger.Instance);
            Assert.NotNull(p);
            _homes.Add(p!.TempHome);
            preps.Add(p);
        }

        // One run wins the refresh race and rotates the token in its own home.
        File.WriteAllText(Path.Combine(preps[3].TempHome, ".credentials.json"), "{\"token\":\"rotated\"}");

        // The rotated token landed in the single home file...
        Assert.Equal("{\"token\":\"rotated\"}", File.ReadAllText(homeCred));
        // ...and every other in-flight run sees it too (shared inode), so none is
        // left holding the dead token.
        foreach (var p in preps)
            Assert.Equal("{\"token\":\"rotated\"}", File.ReadAllText(Path.Combine(p.TempHome, ".credentials.json")));

        // Tearing every per-run home down leaves the live home credential intact.
        foreach (var p in preps) p.Dispose();
        Assert.True(File.Exists(homeCred));
        Assert.Equal("{\"token\":\"rotated\"}", File.ReadAllText(homeCred));
    }

    [Fact]
    public void Dispose_WithLinkedCredential_LeavesHomeFileIntact()
    {
        // Teardown deletes the temp home recursively; deleting the credential
        // LINK must remove only the extra directory entry, never the home file.
        var userHome = NewUserHome();
        var homeCred = Path.Combine(userHome, ".claude", ".credentials.json");
        WriteFile(homeCred, "{\"token\":\"live\"}");

        var prep = CleanContextPreparer.PrepareClaude(userHome, NullLogger.Instance);
        Assert.NotNull(prep);
        var home = prep!.TempHome;

        prep.Dispose();

        Assert.False(Directory.Exists(home));
        Assert.True(File.Exists(homeCred));
        Assert.Equal("{\"token\":\"live\"}", File.ReadAllText(homeCred));
    }

    [Fact]
    public void PrepareClaude_Settings_IsCopiedSnapshot_NotSharedWithHome()
    {
        // settings.json is context, not credentials: it must stay an independent
        // copy so a clean run cannot mutate the operator's base config back home.
        var userHome = NewUserHome();
        WriteFile(Path.Combine(userHome, ".claude", ".credentials.json"), "{}");
        var homeSettings = Path.Combine(userHome, ".claude", "settings.json");
        WriteFile(homeSettings, "{\"a\":1}");

        using var prep = CleanContextPreparer.PrepareClaude(userHome, NullLogger.Instance);
        Assert.NotNull(prep);
        _homes.Add(prep!.TempHome);

        File.WriteAllText(Path.Combine(prep.TempHome, "settings.json"), "{\"a\":2}");
        Assert.Equal("{\"a\":1}", File.ReadAllText(homeSettings));
    }

    [Fact]
    public void PrepareCodex_AuthSharedByLink_RefreshWritesThroughToHome()
    {
        // Codex rotates its ChatGPT OAuth token in auth.json exactly like Claude,
        // so auth.json is shared by link too; config.toml stays a copy.
        var userHome = NewUserHome();
        var homeAuth = Path.Combine(userHome, ".codex", "auth.json");
        WriteFile(homeAuth, "{\"key\":\"old\"}");
        WriteFile(Path.Combine(userHome, ".codex", "config.toml"), "model = \"gpt-5-codex\"");

        using var prep = CleanContextPreparer.PrepareCodex(userHome, NullLogger.Instance);
        Assert.NotNull(prep);
        _homes.Add(prep!.TempHome);

        File.WriteAllText(Path.Combine(prep.TempHome, "auth.json"), "{\"key\":\"new\"}");
        Assert.Equal("{\"key\":\"new\"}", File.ReadAllText(homeAuth));
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
