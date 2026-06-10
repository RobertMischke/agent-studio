using Microsoft.Extensions.Logging;

using AgentStudio.Shared;

namespace AgentStudio.Cli;

/// <summary>
/// One run's <b>clean context</b>: a freshly created, per-run config home for a
/// CLI plus the env override that points the CLI at it (T1b / ASS-1742). Owned
/// by the run's <c>ProcInfo</c>; disposing it tears the temp home down.
/// <para>
/// "clean" is not a CLI flag — it is the absence of the operator's accumulated
/// state. Each adapter implements it by relocating the CLI's whole config home
/// (Claude <c>CLAUDE_CONFIG_DIR</c>, Codex <c>CODEX_HOME</c>) to this temp dir,
/// into which only the auth + base config are seeded. Session history, memory,
/// and project state are deliberately left behind so the run sees only the
/// prompt plus the versioned repo files. Repo instruction files
/// (<c>AGENTS.md</c> / <c>CLAUDE.md</c>) are loaded from the checkout, not the
/// home, so they stay active regardless of mode.
/// </para>
/// </summary>
public sealed class CleanContextPreparation : IDisposable
{
    private readonly ILogger? _logger;
    private int _disposed;

    public CleanContextPreparation(
        string cliType,
        string tempHome,
        IReadOnlyDictionary<string, string> envOverrides,
        IReadOnlyList<CliContextSource> sources,
        ILogger? logger = null)
    {
        CliType = cliType;
        TempHome = tempHome;
        EnvOverrides = envOverrides;
        Sources = sources;
        _logger = logger;
    }

    /// <summary>The CLI this clean home was prepared for (one of <see cref="CliTypes"/>).</summary>
    public string CliType { get; }

    /// <summary>Absolute path of the per-run temp config home.</summary>
    public string TempHome { get; }

    /// <summary>Env var(s) to inject into the child so the CLI reads the temp home.</summary>
    public IReadOnlyDictionary<string, string> EnvOverrides { get; }

    /// <summary>
    /// Context sources describing the temp home + seeded files, surfaced in the
    /// T1a execution-context panel so an operator can see the run used isolated
    /// state (and exactly which temp paths).
    /// </summary>
    public IReadOnlyList<CliContextSource> Sources { get; }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (Directory.Exists(TempHome))
                Directory.Delete(TempHome, recursive: true);
        }
        catch (Exception ex)
        {
            // A leaked temp home is a minor disk-hygiene issue, never a run
            // failure: the OS temp dir is reclaimed eventually and the next run
            // gets its own fresh Guid-suffixed home anyway.
            _logger?.LogDebug(ex, "Failed to clean up clean-context temp home {Path}", TempHome);
        }
    }
}

/// <summary>
/// Pure-ish builder for a CLI's <see cref="CleanContextPreparation"/>: creates
/// the per-run temp home, copies only the auth + base-config allowlist into it,
/// and reports the resulting paths. Kept side-effect-light (it only touches a
/// brand-new temp dir under <see cref="Path.GetTempPath"/>) so it is directly
/// unit-testable with an injected fake home.
/// </summary>
public static class CleanContextPreparer
{
    /// <summary>
    /// Files copied from <c>~/.claude</c> into a clean <c>CLAUDE_CONFIG_DIR</c>:
    /// the OAuth credentials and the base settings. Deliberately excludes
    /// <c>projects/</c> (per-cwd session transcripts), <c>history.jsonl</c>, and
    /// <c>CLAUDE.md</c> (user memory) so a clean run carries no accumulated state.
    /// </summary>
    private static readonly string[] ClaudeSeedFiles = [".credentials.json", "settings.json"];

    /// <summary>
    /// Files copied from <c>~/.codex</c> into a clean <c>CODEX_HOME</c>: the auth
    /// token and the base config. Excludes <c>sessions/</c> and
    /// <c>history.jsonl</c>.
    /// </summary>
    private static readonly string[] CodexSeedFiles = ["auth.json", "config.toml"];

    /// <summary>
    /// Build the Claude clean context (<c>CLAUDE_CONFIG_DIR</c> redirect).
    /// <paramref name="userHome"/> is the user profile root (USERPROFILE / HOME);
    /// the source config dir is <c>{userHome}/.claude</c>. Returns null only when
    /// the temp home cannot be created (clean is then impossible and the caller
    /// falls back to shared).
    /// </summary>
    public static CleanContextPreparation? PrepareClaude(string? userHome, ILogger? logger = null)
    {
        var source = string.IsNullOrWhiteSpace(userHome) ? null : Path.Combine(userHome, ".claude");
        return Prepare(CliTypes.Claude, "CLAUDE_CONFIG_DIR", source, ClaudeSeedFiles, logger);
    }

    /// <summary>
    /// Build the Codex clean context (<c>CODEX_HOME</c> redirect). The source
    /// config dir is <c>{userHome}/.codex</c>.
    /// </summary>
    public static CleanContextPreparation? PrepareCodex(string? userHome, ILogger? logger = null)
    {
        var source = string.IsNullOrWhiteSpace(userHome) ? null : Path.Combine(userHome, ".codex");
        return Prepare(CliTypes.Codex, "CODEX_HOME", source, CodexSeedFiles, logger);
    }

    private static CleanContextPreparation? Prepare(
        string cliType,
        string envVar,
        string? sourceDir,
        IReadOnlyList<string> seedFiles,
        ILogger? logger)
    {
        string tempHome;
        try
        {
            tempHome = Path.Combine(
                Path.GetTempPath(),
                "atp-clean-context",
                $"{cliType}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempHome);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not create clean-context temp home for {Cli}; falling back to shared", cliType);
            return null;
        }

        var sources = new List<CliContextSource>
        {
            new()
            {
                Kind = CliContextSourceKinds.Env,
                Label = envVar,
                Path = tempHome,
                Exists = true,
                Detail = "isolated clean-context home seeded for this run",
            },
        };

        foreach (var rel in seedFiles)
        {
            if (string.IsNullOrWhiteSpace(sourceDir)) break;
            var src = Path.Combine(sourceDir, rel);
            var dst = Path.Combine(tempHome, rel);
            try
            {
                if (!File.Exists(src)) continue;
                var dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                File.Copy(src, dst, overwrite: true);
                sources.Add(new CliContextSource
                {
                    Kind = CliContextSourceKinds.GlobalConfig,
                    Label = $"Seeded {rel}",
                    Path = dst,
                    Exists = true,
                    Detail = $"copied from {src}",
                });
            }
            catch (Exception ex)
            {
                // A failed seed is not fatal: auth may come from an env var
                // (ANTHROPIC_API_KEY / CODEX auth) instead of the file, so the
                // clean run can still succeed. Note it for diagnostics only.
                logger?.LogDebug(ex, "Could not seed {File} into clean {Cli} home", rel, cliType);
            }
        }

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [envVar] = tempHome };
        return new CleanContextPreparation(cliType, tempHome, env, sources, logger);
    }
}
