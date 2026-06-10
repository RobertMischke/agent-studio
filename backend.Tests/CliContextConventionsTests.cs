using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the pure, filesystem-probing convention builder that derives the
/// context sources a CLI loads beyond the prompt (ASS-1739 / T1a). This is the
/// only source of truth for Codex / Copilot / Gemini and the merged-under base
/// for Claude, so a regression here silently mis-reports what the agent saw.
/// Tests build a throwaway directory tree, point the builder at it, and assert
/// the memory / instruction chains, session store, and global config resolve to
/// the right <see cref="CliContextSource.Kind"/> with honest existence flags.
/// </summary>
public class CliContextConventionsTests : IDisposable
{
    private readonly string _root;
    private readonly string _home;
    private readonly string _cwd;

    public CliContextConventionsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cli-ctx-conv-" + Guid.NewGuid().ToString("N"));
        _home = Path.Combine(_root, "home");
        _cwd = Path.Combine(_root, "work", "repo", "nested");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_cwd);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Claude_WalksMemoryChainNearestFirstAndAddsHomeSources()
    {
        // CLAUDE.md at two ancestors of cwd (nested + repo); not at "work".
        var atNested = Path.Combine(_cwd, "CLAUDE.md");
        var atRepo = Path.Combine(_root, "work", "repo", "CLAUDE.md");
        Write(atNested);
        Write(atRepo);
        Write(Path.Combine(_home, ".claude", "CLAUDE.md"));      // user memory
        Write(Path.Combine(_home, ".claude", "settings.json"));  // global config

        var sources = CliContextConventions.For("claude", _cwd, _home);

        // Two project-memory entries, nearest first.
        var memory = sources.Where(s => s.Kind == CliContextSourceKinds.Memory).ToList();
        Assert.Equal(3, memory.Count); // 2 project chain + 1 user memory
        Assert.Equal(atNested, memory[0].Path);
        Assert.Equal(atRepo, memory[1].Path);
        Assert.All(memory, m => Assert.True(m.Exists));

        Assert.Contains(sources, s => s.Kind == CliContextSourceKinds.Session);
        Assert.Contains(sources, s =>
            s.Kind == CliContextSourceKinds.GlobalConfig &&
            s.Path == Path.Combine(_home, ".claude", "settings.json") &&
            s.Exists == true);
    }

    [Fact]
    public void Claude_NullHome_SkipsHomeRootedSources()
    {
        Write(Path.Combine(_cwd, "CLAUDE.md"));

        var sources = CliContextConventions.For("claude", _cwd, home: null);

        Assert.Single(sources); // only the one project-memory file
        Assert.Equal(CliContextSourceKinds.Memory, sources[0].Kind);
        Assert.DoesNotContain(sources, s => s.Kind == CliContextSourceKinds.Session);
        Assert.DoesNotContain(sources, s => s.Kind == CliContextSourceKinds.GlobalConfig);
    }

    [Fact]
    public void Claude_AbsentMemoryFiles_AreNotAddedToChain()
    {
        // No CLAUDE.md anywhere in the cwd chain; home has nothing either.
        var sources = CliContextConventions.For("claude", _cwd, _home);

        Assert.DoesNotContain(sources, s => s.Kind == CliContextSourceKinds.Memory);
        // Session store is still reported (with Exists=false) so the panel can
        // show where the CLI would read/write transcripts.
        var session = Assert.Single(sources, s => s.Kind == CliContextSourceKinds.Session);
        Assert.False(session.Exists);
    }

    [Fact]
    public void Codex_WalksAgentsMdChainAndAddsHomeConfig()
    {
        var saved = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", null);
        try
        {
            Write(Path.Combine(_cwd, "AGENTS.md"));
            Write(Path.Combine(_home, ".codex", "config.toml"));
            Directory.CreateDirectory(Path.Combine(_home, ".codex", "sessions"));

            var sources = CliContextConventions.For("codex", _cwd, _home);

            var memory = Assert.Single(sources, s => s.Kind == CliContextSourceKinds.Memory);
            Assert.Equal(Path.Combine(_cwd, "AGENTS.md"), memory.Path);

            var session = Assert.Single(sources, s => s.Kind == CliContextSourceKinds.Session);
            Assert.True(session.Exists);

            Assert.Contains(sources, s =>
                s.Kind == CliContextSourceKinds.GlobalConfig &&
                s.Path == Path.Combine(_home, ".codex", "config.toml") &&
                s.Exists == true);
            // No CODEX_HOME env source when the variable is unset.
            Assert.DoesNotContain(sources, s => s.Kind == CliContextSourceKinds.Env);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", saved);
        }
    }

    [Fact]
    public void Codex_CodexHomeEnv_OverridesAndIsReported()
    {
        var saved = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = Path.Combine(_root, "custom-codex");
        Directory.CreateDirectory(codexHome);
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        try
        {
            Write(Path.Combine(codexHome, "config.toml"));

            var sources = CliContextConventions.For("codex", _cwd, _home);

            var env = Assert.Single(sources, s => s.Kind == CliContextSourceKinds.Env);
            Assert.Equal("CODEX_HOME", env.Label);
            Assert.Equal(codexHome, env.Path);

            // Global config resolved under the override, not ~/.codex.
            Assert.Contains(sources, s =>
                s.Kind == CliContextSourceKinds.GlobalConfig &&
                s.Path == Path.Combine(codexHome, "config.toml"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", saved);
        }
    }

    [Fact]
    public void Copilot_IncludesInstructionFileAndProjectMemory()
    {
        Write(Path.Combine(_cwd, ".github", "copilot-instructions.md"));
        Write(Path.Combine(_cwd, "AGENTS.md"));
        Write(Path.Combine(_home, ".copilot", "config.json"));

        var sources = CliContextConventions.For("copilot", _cwd, _home);

        Assert.Contains(sources, s =>
            s.Kind == CliContextSourceKinds.InstructionFile &&
            s.Path == Path.Combine(_cwd, ".github", "copilot-instructions.md") &&
            s.Exists == true);
        Assert.Contains(sources, s =>
            s.Kind == CliContextSourceKinds.Memory &&
            s.Path == Path.Combine(_cwd, "AGENTS.md"));
        Assert.Contains(sources, s =>
            s.Kind == CliContextSourceKinds.GlobalConfig &&
            s.Path == Path.Combine(_home, ".copilot", "config.json") &&
            s.Exists == true);
    }

    [Fact]
    public void Gemini_FindsGeminiMdChainAndUserMemory()
    {
        Write(Path.Combine(_cwd, "GEMINI.md"));
        Write(Path.Combine(_home, ".gemini", "GEMINI.md"));
        Write(Path.Combine(_home, ".gemini", "settings.json"));

        var sources = CliContextConventions.For("gemini", _cwd, _home);

        var memory = sources.Where(s => s.Kind == CliContextSourceKinds.Memory).ToList();
        Assert.Equal(2, memory.Count); // project + user
        Assert.Equal(Path.Combine(_cwd, "GEMINI.md"), memory[0].Path);
        Assert.Contains(sources, s =>
            s.Kind == CliContextSourceKinds.GlobalConfig &&
            s.Path == Path.Combine(_home, ".gemini", "settings.json") &&
            s.Exists == true);
    }

    [Fact]
    public void UnknownCli_FallsBackToCopilotConventions()
    {
        // CliTypes.Normalize defaults any unrecognized value to Copilot, so the
        // builder must produce Copilot's convention set rather than an empty
        // list - the switch default is unreachable in practice.
        Write(Path.Combine(_cwd, ".github", "copilot-instructions.md"));

        var sources = CliContextConventions.For("totally-unknown", _cwd, _home);

        Assert.Contains(sources, s => s.Kind == CliContextSourceKinds.InstructionFile);
    }

    [Fact]
    public void NullCwd_SkipsProjectChainButKeepsHomeSources()
    {
        Write(Path.Combine(_home, ".claude", "settings.json"));

        var sources = CliContextConventions.For("claude", cwd: null, home: _home);

        Assert.DoesNotContain(sources, s => s.Kind == CliContextSourceKinds.Memory && s.Label == "Project memory");
        Assert.Contains(sources, s => s.Kind == CliContextSourceKinds.GlobalConfig);
    }
}
