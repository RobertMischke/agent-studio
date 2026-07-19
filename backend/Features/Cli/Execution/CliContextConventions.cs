using AgentStudio.Shared;

namespace AgentStudio.Cli;

/// <summary>
/// Pure, dependency-free builder for the <b>convention-derived</b> context
/// sources a CLI loads for a run - the memory / instruction-file chain it walks
/// up from the working directory, the session store it reads / writes, and its
/// global config directory. Each CLI documents these path conventions; this
/// helper encodes them and probes the filesystem so the read-only
/// execution-context surface (ASS-1739 / T1a) can report which of them actually
/// exist on disk for a given run.
/// <para>
/// This is the only source of truth for Codex / Gemini (they emit no
/// init frame). For Claude these convention sources are merged <i>under</i> the
/// richer init-frame data the CLI itself reports. Probing never mutates: it only
/// calls <see cref="File.Exists"/> / <see cref="Directory.Exists"/>.
/// </para>
/// </summary>
public static class CliContextConventions
{
    /// <summary>
    /// How far up the directory tree to walk looking for memory / instruction
    /// files. Bounds the probe so a deeply nested cwd cannot produce an
    /// unbounded source list; in practice the repo root is within a few levels.
    /// </summary>
    private const int MaxAncestorDepth = 12;

    /// <summary>
    /// Build the convention sources for <paramref name="cliType"/> given the
    /// run's working directory and the user-profile home. <paramref name="home"/>
    /// is injected (rather than read from the environment here) so the builder
    /// stays pure and unit-testable; callers pass
    /// <c>USERPROFILE</c> / <c>HOME</c>. A null / empty home skips the
    /// home-rooted sources (global config, user memory, session store).
    /// </summary>
    public static List<CliContextSource> For(string? cliType, string? cwd, string? home)
    {
        var cli = CliTypes.Normalize(cliType);
        return cli switch
        {
            CliTypes.Claude => Claude(cwd, home),
            CliTypes.Codex => Codex(cwd, home),
            CliTypes.Gemini => Gemini(cwd, home),
            _ => [],
        };
    }

    private static List<CliContextSource> Claude(string? cwd, string? home)
    {
        var sources = new List<CliContextSource>();
        AddMemoryChain(sources, cwd, "CLAUDE.md", "Project memory");
        if (!string.IsNullOrWhiteSpace(home))
        {
            AddMemoryFile(sources, Path.Combine(home, ".claude", "CLAUDE.md"), "User memory");
            var projects = Path.Combine(home, ".claude", "projects");
            AddSessionStore(sources, ClaudeProjectSessionDir(projects, cwd) ?? projects, "Session store");
            AddGlobalConfig(sources, Path.Combine(home, ".claude", "settings.json"), "Global config");
            AddGlobalConfig(sources, Path.Combine(home, ".claude"), "Global config dir");
        }
        return sources;
    }

    private static List<CliContextSource> Codex(string? cwd, string? home)
    {
        var sources = new List<CliContextSource>();
        AddMemoryChain(sources, cwd, "AGENTS.md", "Project memory");
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var root = !string.IsNullOrWhiteSpace(codexHome)
            ? codexHome
            : (!string.IsNullOrWhiteSpace(home) ? Path.Combine(home, ".codex") : null);
        if (!string.IsNullOrWhiteSpace(codexHome))
            sources.Add(new CliContextSource
            {
                Kind = CliContextSourceKinds.Env,
                Label = "CODEX_HOME",
                Path = codexHome,
                Exists = SafeDirExists(codexHome),
                Detail = "overrides the default ~/.codex location",
            });
        if (!string.IsNullOrWhiteSpace(root))
        {
            AddSessionStore(sources, Path.Combine(root, "sessions"), "Session store");
            AddGlobalConfig(sources, Path.Combine(root, "config.toml"), "Global config");
        }
        return sources;
    }

    private static List<CliContextSource> Gemini(string? cwd, string? home)
    {
        var sources = new List<CliContextSource>();
        AddMemoryChain(sources, cwd, "GEMINI.md", "Project memory");
        if (!string.IsNullOrWhiteSpace(home))
        {
            AddMemoryFile(sources, Path.Combine(home, ".gemini", "GEMINI.md"), "User memory");
            AddGlobalConfig(sources, Path.Combine(home, ".gemini", "settings.json"), "Global config");
            AddGlobalConfig(sources, Path.Combine(home, ".gemini"), "Global config dir");
        }
        return sources;
    }

    // --- shared builders -------------------------------------------------

    private static void AddMemoryChain(List<CliContextSource> sources, string? cwd, string fileName, string label)
        => AddUpwardChain(sources, cwd, fileName, label, CliContextSourceKinds.Memory);

    /// <summary>
    /// Walk from <paramref name="cwd"/> up to the filesystem root adding a
    /// source for every ancestor that has <paramref name="relPath"/>, nearest
    /// first. Only existing files are added (the chain is "what was loaded",
    /// not "every place it could have lived").
    /// </summary>
    private static void AddUpwardChain(List<CliContextSource> sources, string? cwd, string relPath, string label, string kind)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return;
        DirectoryInfo? dir;
        try { dir = new DirectoryInfo(cwd); }
        catch { return; }

        var depth = 0;
        while (dir != null && depth++ < MaxAncestorDepth)
        {
            var candidate = Path.Combine(dir.FullName, relPath);
            if (SafeFileExists(candidate))
                sources.Add(new CliContextSource
                {
                    Kind = kind,
                    Label = label,
                    Path = candidate,
                    Exists = true,
                });
            dir = dir.Parent;
        }
    }

    private static void AddMemoryFile(List<CliContextSource> sources, string path, string label)
    {
        var exists = SafeFileExists(path);
        if (exists)
            sources.Add(new CliContextSource
            {
                Kind = CliContextSourceKinds.Memory,
                Label = label,
                Path = path,
                Exists = true,
            });
    }

    private static void AddSessionStore(List<CliContextSource> sources, string path, string label)
        => sources.Add(new CliContextSource
        {
            Kind = CliContextSourceKinds.Session,
            Label = label,
            Path = path,
            Exists = SafeDirExists(path),
        });

    private static void AddGlobalConfig(List<CliContextSource> sources, string path, string label)
        => sources.Add(new CliContextSource
        {
            Kind = CliContextSourceKinds.GlobalConfig,
            Label = label,
            Path = path,
            Exists = SafeFileExists(path) || SafeDirExists(path),
        });

    /// <summary>
    /// Resolve the Claude per-project session directory the same way the CLI
    /// encodes it (path separators / colons -&gt; '-'); returns null when the
    /// parent <c>projects</c> dir or the encoded child does not exist so the
    /// caller falls back to the parent.
    /// </summary>
    private static string? ClaudeProjectSessionDir(string projects, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !SafeDirExists(projects)) return null;
        var encoded = cwd.Replace('\\', '-').Replace(":", "-");
        var candidate = Path.Combine(projects, encoded);
        return SafeDirExists(candidate) ? candidate : null;
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    private static bool SafeDirExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }
}
