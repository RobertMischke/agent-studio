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
/// the per-run temp home, seeds only the auth + base-config allowlist into it,
/// and reports the resulting paths. The credential file is <b>shared by link</b>
/// (hard link / symlink) back to the operator's one home file so a mid-run OAuth
/// refresh persists centrally instead of dying with the temp home (AGT-2066);
/// base config is copied as an isolated snapshot. Preparation only creates
/// entries under a brand-new temp dir beneath <see cref="Path.GetTempPath"/>
/// (the credential link's other end is the pre-existing home file, never
/// rewritten here), so it stays directly unit-testable with an injected fake home.
/// </summary>
public static class CleanContextPreparer
{
    /// <summary>
    /// One file seeded from the operator's config home into a clean home.
    /// <para>
    /// <see cref="ShareByLink"/> decides how: credential files whose OAuth token
    /// the CLI rotates in place (<c>.credentials.json</c>, Codex <c>auth.json</c>)
    /// are <b>shared by link</b> so a refresh writes through to the one home file;
    /// base-config files (<c>settings.json</c>, <c>config.toml</c>) are <b>copied</b>
    /// as an isolated snapshot the clean run may read but must not mutate back.
    /// </para>
    /// </summary>
    private readonly record struct CleanContextSeed(string RelativePath, bool ShareByLink);

    /// <summary>
    /// Files seeded from <c>~/.claude</c> into a clean <c>CLAUDE_CONFIG_DIR</c>:
    /// the OAuth credentials (shared by link) and the base settings (copied).
    /// Deliberately excludes <c>projects/</c> (per-cwd session transcripts),
    /// <c>history.jsonl</c>, and <c>CLAUDE.md</c> (user memory) so a clean run
    /// carries no accumulated state.
    /// </summary>
    private static readonly CleanContextSeed[] ClaudeSeedFiles =
    [
        new(".credentials.json", ShareByLink: true),
        new("settings.json", ShareByLink: false),
    ];

    /// <summary>
    /// Files seeded from <c>~/.codex</c> into a clean <c>CODEX_HOME</c>: the auth
    /// token (shared by link - Codex rotates its ChatGPT OAuth token in
    /// <c>auth.json</c> just like Claude) and the base config (copied). Excludes
    /// <c>sessions/</c> and <c>history.jsonl</c>.
    /// </summary>
    private static readonly CleanContextSeed[] CodexSeedFiles =
    [
        new("auth.json", ShareByLink: true),
        new("config.toml", ShareByLink: false),
    ];

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
        IReadOnlyList<CleanContextSeed> seedFiles,
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

        foreach (var seed in seedFiles)
        {
            if (string.IsNullOrWhiteSpace(sourceDir)) break;
            var rel = seed.RelativePath;
            var src = Path.Combine(sourceDir, rel);
            var dst = Path.Combine(tempHome, rel);
            try
            {
                if (!File.Exists(src)) continue;
                var dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

                if (seed.ShareByLink)
                {
                    // Credential file: link, don't copy, so a mid-run OAuth
                    // refresh writes through to the single home file (AGT-2066).
                    var linked = TryLinkSharedFile(src, dst, cliType, rel, logger);
                    sources.Add(new CliContextSource
                    {
                        Kind = CliContextSourceKinds.GlobalConfig,
                        Label = linked ? $"Linked {rel}" : $"Seeded {rel}",
                        Path = dst,
                        Exists = true,
                        Detail = linked
                            ? $"shared link to {src} so an OAuth refresh lands in the one home file, not a throwaway per-run copy"
                            : $"copied from {src} (link unavailable; parallel refreshes may still drift)",
                    });
                }
                else
                {
                    // Base config: an isolated snapshot the clean run reads but
                    // must not mutate back into the operator's home.
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

    /// <summary>
    /// Link <paramref name="dst"/> (in the per-run clean home) to the operator's
    /// single home credential file <paramref name="src"/> instead of copying it,
    /// so a token refresh the CLI performs mid-run is written through to the one
    /// home file that every concurrent run and every later launch reads.
    /// <para>
    /// This closes the "OAuth token roulette" (incident 2026-07-10, AGT-2066):
    /// with per-run <em>copies</em>, N parallel runs that hit an expired token
    /// each refresh the same rotating refresh token, the provider validates only
    /// the first, its new token is written into a temp home that is deleted when
    /// the run ends, and the home file keeps the now-dead token so every later
    /// launch fails. A shared link makes the winning refresh persist centrally.
    /// </para>
    /// <para>
    /// Windows uses a hard link (no elevation on the same NTFS volume); other
    /// platforms use a symlink. Both are transparent to the CLI's in-place
    /// credential write, and tearing down the per-run home only removes the extra
    /// directory entry, never the home file's data. Returns <c>false</c> (the
    /// caller then falls back to a plain copy, the pre-AGT-2066 behaviour) when a
    /// link cannot be created - cross-volume temp dir, missing privilege, or an
    /// unsupported filesystem - so a clean run is never blocked by link failure,
    /// only degraded to the old copy behaviour for that run.
    /// </para>
    /// </summary>
    private static bool TryLinkSharedFile(string src, string dst, string cliType, string rel, ILogger? logger)
    {
        try
        {
            // Fresh Guid-suffixed temp home, so dst should not exist; be
            // defensive in case a retry reused the path.
            if (File.Exists(dst)) File.Delete(dst);

            if (OperatingSystem.IsWindows())
            {
                if (CreateHardLinkW(dst, src, IntPtr.Zero))
                {
                    logger?.LogInformation(
                        "Clean-context {Cli}: hard-linked {File} to shared home file {Src}; OAuth refreshes land centrally",
                        cliType, rel, src);
                    return true;
                }
                var err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                logger?.LogWarning(
                    "Clean-context {Cli}: could not hard-link {File} to {Src} (Win32 error {Err}); falling back to copy - parallel OAuth refreshes may drift",
                    cliType, rel, src, err);
            }
            else
            {
                File.CreateSymbolicLink(dst, src);
                logger?.LogInformation(
                    "Clean-context {Cli}: symlinked {File} to shared home file {Src}; OAuth refreshes land centrally",
                    cliType, rel, src);
                return true;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "Clean-context {Cli}: linking {File} to {Src} failed; falling back to copy - parallel OAuth refreshes may drift",
                cliType, rel, src);
        }

        // Fallback: copy (correct for a single run; only the cross-run
        // parallel-refresh drift is not covered). A throw here propagates to the
        // caller's non-fatal seed handler.
        File.Copy(src, dst, overwrite: true);
        return false;
    }

    /// <summary>
    /// Win32 <c>CreateHardLinkW</c>. Creates <paramref name="lpFileName"/> as a new
    /// hard link to the existing file <paramref name="lpExistingFileName"/> on the
    /// same volume. Preferred over a symlink on Windows because it needs no
    /// elevation / Developer Mode. Called only under
    /// <see cref="OperatingSystem.IsWindows"/>.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
