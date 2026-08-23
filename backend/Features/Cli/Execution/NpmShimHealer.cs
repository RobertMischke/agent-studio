using System.Diagnostics;

namespace AgentStudio.Cli;

/// <summary>
/// Self-heal for half-installed npm CLI shims on Windows (the
/// <c>infra-cli-broken</c> category in
/// <c>docs/system/contracts/agent-contract-pattern.md</c>).
///
/// <para>
/// Background. npm's atomic-rename pattern (write
/// <c>.&lt;name&gt;-&lt;random&gt;</c>, then rename to <c>&lt;name&gt;</c>) fails
/// on Windows when the target is locked, leaving orphans like
/// <c>.claude-2shlnT4k</c>, <c>.claude.cmd-A8DH7lDq</c>,
/// <c>.claude.ps1-Phb6s52t</c>. The Anthropic <c>claude-code</c> postinstall
/// additionally swaps a ~500-byte stub for the real ~254 MB binary; an
/// interrupt mid-postinstall leaves the stub in place AND can rename the
/// source binary to <c>claude.exe.old.&lt;timestamp&gt;</c> inside the platform
/// package. A racing auto-updater can put the install back into that shape
/// every few minutes, so a pickup that worked at boot can fail at minute
/// twelve through no fault of the job.
/// </para>
///
/// <para>
/// This is the in-process C# port of <c>tools/check-cli-shims.sh</c>. The
/// shell script remains the boot-time pre-flight; this class is the
/// per-spawn last-line defence inside the runner. Same idempotence
/// guarantee: silent when nothing is wrong, returns a list of actions when
/// it fixed something. Exit-equivalent: <see cref="HealOutcome.Available"/>
/// is the smoke-test verdict of <c>claude --version</c> after the repair
/// pass.
/// </para>
/// </summary>
public sealed record HealOutcome(
    bool Available,
    IReadOnlyList<string> Actions,
    string? Error)
{
    /// <summary>Which repair shape the final smoke-test failure fell into.
    /// <see cref="ShimRepairCategory.Healthy"/> when <see cref="Available"/>
    /// is true or the host is non-Windows (steps 1-4 never needed to run).</summary>
    public ShimRepairCategory Category { get; init; } = ShimRepairCategory.Healthy;

    /// <summary>claude-code package.json <c>version</c> read before the
    /// <c>npm install -g</c> fallback ran, when it ran. Null otherwise -
    /// this is the AGT-2673 root-cause signal (2.1.231 -&gt; 2.1.234 across
    /// the two 2026-08 incidents), not a general-purpose field.</summary>
    public string? VersionBefore { get; init; }

    /// <summary>Same, read after the fallback. Null if the fallback did not run.</summary>
    public string? VersionAfter { get; init; }

    /// <summary>True once step 6 (see below) actually invoked <c>npm install -g</c>,
    /// as opposed to skipping it (already healthy, wrong category, or rate-limited).</summary>
    public bool NpmInstallAttempted { get; init; }

    /// <summary>True when step 6 was eligible (category
    /// <see cref="ShimRepairCategory.ShimMissingPackagePresent"/>) but skipped
    /// because an attempt already ran within <see cref="NpmShimRepairPolicy.NpmInstallCooldown"/>.</summary>
    public bool RateLimited { get; init; }
}

/// <summary>
/// Which repair shape a still-broken <c>claude</c> shim falls into once
/// steps 1-4 (orphan rename, platform/wrapper binary restore, postinstall
/// re-run, staging cleanup) have run and the smoke test still fails.
/// </summary>
public enum ShimRepairCategory
{
    /// <summary>The shim already works (or the host is non-Windows); no
    /// further repair is needed.</summary>
    Healthy,

    /// <summary>The <c>claude</c> npm package is present under
    /// <c>node_modules/@anthropic-ai/claude-code</c> but the top-level bin
    /// shim (<c>claude.cmd</c> etc.) is still missing or non-functional
    /// after steps 1-4. This is the shape steps 1-4 cannot fix on their
    /// own: npm's own bin-shim linking never re-ran, which only
    /// <c>npm install -g</c> regenerates. Eligible for the bounded,
    /// rate-limited fallback.</summary>
    ShimMissingPackagePresent,

    /// <summary>No <c>node_modules/@anthropic-ai/claude-code</c> directory
    /// at all - a real uninstall, not a broken shim. Never auto-repaired:
    /// running a fresh global install on a host where the package was
    /// deliberately removed is an operator decision, not an infra self-heal.</summary>
    TrulyUninstalled,
}

/// <summary>
/// Pure decision library for the <c>npm install -g</c> fallback (step 6):
/// which category is eligible, and whether the cooldown has elapsed. No I/O,
/// no clock reads - same shape as <c>RapidCrashBreaker</c> so the policy is
/// unit-testable without Windows or a filesystem.
/// </summary>
public static class NpmShimRepairPolicy
{
    /// <summary>One <c>npm install -g</c> attempt per host per hour. A
    /// racing auto-updater can put the install back into a broken shape
    /// every few minutes (the 2026-08 incidents); without a bound, a
    /// pickup loop hammering the pre-spawn health check would turn one
    /// broken shim into a storm of global npm installs.</summary>
    public static readonly TimeSpan NpmInstallCooldown = TimeSpan.FromHours(1);

    public static ShimRepairCategory Classify(bool shimAvailable, bool packagePresent)
        => shimAvailable
            ? ShimRepairCategory.Healthy
            : packagePresent
                ? ShimRepairCategory.ShimMissingPackagePresent
                : ShimRepairCategory.TrulyUninstalled;

    public static bool IsNpmInstallAllowed(DateTime? lastAttemptUtc, DateTime nowUtc)
        => lastAttemptUtc is null || nowUtc - lastAttemptUtc.Value >= NpmInstallCooldown;
}

public static class NpmShimHealer
{
    /// <summary>Marker file recording the UTC timestamp of the last
    /// <c>npm install -g</c> fallback attempt, so the cooldown survives a
    /// backend restart. Lives next to the shims themselves (same directory
    /// the check-cli-shims.sh fallback cache file uses).</summary>
    internal const string NpmInstallMarkerFileName = ".atp-npm-install-repair";

    /// <summary>
    /// Repair the <c>claude</c> npm-shim install on Windows and smoke-test
    /// the resulting <c>claude.cmd</c>. No-op on non-Windows hosts (the
    /// failure mode is Windows-specific).
    /// </summary>
    public static async Task<HealOutcome> TryHealClaudeAsync(
        ILogger logger,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new HealOutcome(true, Array.Empty<string>(), null);
        }

        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrEmpty(appData))
        {
            return new HealOutcome(false, Array.Empty<string>(),
                "APPDATA env var is unset; cannot locate npm global bin");
        }
        var npmBin = Path.Combine(appData, "npm");
        if (!Directory.Exists(npmBin))
        {
            return new HealOutcome(false, Array.Empty<string>(),
                $"npm global bin not found at '{npmBin}'");
        }

        var actions = new List<string>();

        // 1. Restore atomic-rename orphan shims for `claude` and `gemini`.
        //    Same npm shim-set is broken by the same race for both CLIs.
        foreach (var cli in new[] { "claude", "gemini" })
        {
            foreach (var ext in new[] { "", ".cmd", ".ps1" })
            {
                var target = Path.Combine(npmBin, cli + ext);
                if (File.Exists(target)) continue;

                string[] orphans;
                try
                {
                    orphans = Directory.GetFiles(npmBin, "." + cli + ext + "-*");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to scan npm bin for orphan shims of {Cli}{Ext}", cli, ext);
                    continue;
                }
                if (orphans.Length == 0) continue;

                var first = orphans[0];
                try
                {
                    File.Move(first, target);
                    actions.Add($"renamed orphan shim {Path.GetFileName(first)} -> {Path.GetFileName(target)}");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to restore shim {Orphan} -> {Target}", first, target);
                }
            }
        }

        // 2. Restore the platform-specific claude.exe when an interrupted
        //    postinstall renamed it to claude.exe.old.<timestamp> and the
        //    canonical claude.exe is missing.
        var platDir = Path.Combine(npmBin, "node_modules", "@anthropic-ai", "claude-code-win32-x64");
        var realExe = Path.Combine(platDir, "claude.exe");
        if (Directory.Exists(platDir) && !File.Exists(realExe))
        {
            string[] olds;
            try { olds = Directory.GetFiles(platDir, "claude.exe.old.*"); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to enumerate platform .old.* files in {Dir}", platDir);
                olds = Array.Empty<string>();
            }
            var newest = olds
                .Select(f => (Path: f, MTime: SafeLastWriteTime(f)))
                .OrderByDescending(t => t.MTime)
                .Select(t => t.Path)
                .FirstOrDefault();
            if (newest is not null)
            {
                try
                {
                    File.Move(newest, realExe);
                    actions.Add($"restored platform binary {Path.GetFileName(newest)} -> claude.exe");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to restore platform binary {From} -> {To}", newest, realExe);
                }
            }
        }

        // 3. Repair the wrapper bin/claude.exe. Three observed failure shapes:
        //    (a) present-but-stub (<4 KB) — interrupted postinstall mid-swap.
        //    (b) missing canonical + sibling claude.exe.old.<ts> — half-completed rename.
        //    (c) missing canonical + no sibling — installer ran preinstall delete before crash.
        //    Shape (b) heals via a rename back (no network / no postinstall needed since
        //    the .old payload is the previously-correct binary); shapes (a) and (c) need
        //    the wrapper's node install.cjs postinstall to fetch / unpack the platform
        //    binary again.
        var wrapDir = Path.Combine(npmBin, "node_modules", "@anthropic-ai", "claude-code");
        var wrapBin = Path.Combine(wrapDir, "bin", "claude.exe");
        var wrapBinDir = Path.Combine(wrapDir, "bin");
        if (Directory.Exists(wrapDir))
        {
            // Shape (b): try the .old.<ts> sibling first.
            if (!File.Exists(wrapBin) && Directory.Exists(wrapBinDir))
            {
                string[] wrapOlds;
                try { wrapOlds = Directory.GetFiles(wrapBinDir, "claude.exe.old.*"); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to enumerate wrapper .old.* files in {Dir}", wrapBinDir);
                    wrapOlds = Array.Empty<string>();
                }
                var newestWrapOld = wrapOlds
                    .Select(f => (Path: f, MTime: SafeLastWriteTime(f)))
                    .OrderByDescending(t => t.MTime)
                    .Select(t => t.Path)
                    .FirstOrDefault();
                if (newestWrapOld is not null)
                {
                    try
                    {
                        File.Move(newestWrapOld, wrapBin);
                        actions.Add($"restored wrapper binary {Path.GetFileName(newestWrapOld)} -> claude.exe");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to restore wrapper binary {From} -> {To}", newestWrapOld, wrapBin);
                    }
                }
            }

            // Shapes (a) and (c): missing OR stub → postinstall.
            var needsPostinstall = false;
            string? reason = null;
            if (!File.Exists(wrapBin))
            {
                needsPostinstall = true;
                reason = "wrapper bin/claude.exe still missing after .old fallback";
            }
            else
            {
                long size = -1;
                try { size = new FileInfo(wrapBin).Length; } catch (Exception __ex) { SilentCatch.Note(__ex, "NpmShimHealer: fall through"); /* fall through */ }
                if (size >= 0 && size < 4096)
                {
                    needsPostinstall = true;
                    reason = $"stub binary at claude-code/bin/claude.exe ({size} bytes)";
                }
            }

            if (needsPostinstall)
            {
                actions.Add($"{reason}, running postinstall");
                var postOk = await TryRunPostInstallAsync(wrapDir, logger, ct);
                actions.Add(postOk ? "postinstall completed" : "postinstall failed (smoke-test below is verdict)");
            }
        }

        // 4. Remove staging-orphan directories left under @anthropic-ai/ by
        //    interrupted npm installs (pattern: .<pkg>-<random>/).
        var anthropicDir = Path.Combine(npmBin, "node_modules", "@anthropic-ai");
        if (Directory.Exists(anthropicDir))
        {
            string[] stagingOrphans;
            try { stagingOrphans = Directory.GetDirectories(anthropicDir, ".*-*"); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to enumerate staging orphans in {Dir}", anthropicDir);
                stagingOrphans = Array.Empty<string>();
            }
            foreach (var orphanDir in stagingOrphans)
            {
                try
                {
                    Directory.Delete(orphanDir, recursive: true);
                    actions.Add($"removed staging orphan {Path.GetFileName(orphanDir)}");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to remove staging orphan {Dir}", orphanDir);
                }
            }
        }

        // 5. Smoke test. The shim is what the OS actually invokes via PATH;
        //    call it directly so we don't depend on PATH ordering.
        var shim = Path.Combine(npmBin, "claude.cmd");
        var (smokeOk, smokeError) = await SmokeTestAsync(shim, ct);
        if (smokeOk)
        {
            return new HealOutcome(true, actions, null);
        }

        // 6. Last-resort fallback: `npm install -g` regenerates npm's own
        // bin-shim linking. Steps 1-4 repair every anthropic-postinstall
        // failure shape (orphan renames, stub swap, staging orphans) but not
        // the case where npm itself never re-linked `claude.cmd` in the
        // global bin - the 2026-08-13 and 2026-08-18 incidents: `claude` gone
        // from PATH, the package present under node_modules, version jumped
        // 2.1.231 -> 2.1.234 (auto-update suspected), only a manual
        // `npm install -g @anthropic-ai/claude-code` fixed it. Bounded to one
        // attempt per hour (NpmShimRepairPolicy.NpmInstallCooldown) so a
        // racing auto-updater cannot turn repeated pre-spawn health checks
        // into an install storm; the caller journals the outcome fields
        // below (AGT-2673).
        var packagePresent = Directory.Exists(wrapDir);
        var category = NpmShimRepairPolicy.Classify(shimAvailable: false, packagePresent);
        if (category != ShimRepairCategory.ShimMissingPackagePresent)
        {
            return new HealOutcome(false, actions, smokeError) { Category = category };
        }

        var versionBefore = ReadPackageVersion(wrapDir);
        var markerPath = Path.Combine(npmBin, NpmInstallMarkerFileName);
        var lastAttempt = ReadLastNpmInstallAttempt(markerPath, logger);
        if (!NpmShimRepairPolicy.IsNpmInstallAllowed(lastAttempt, DateTime.UtcNow))
        {
            return new HealOutcome(
                false,
                actions,
                $"{smokeError}; npm install -g skipped (rate-limited, last attempt {lastAttempt:o})")
            {
                Category = category,
                VersionBefore = versionBefore,
                RateLimited = true,
            };
        }

        WriteLastNpmInstallAttempt(markerPath, DateTime.UtcNow, logger);
        actions.Add("running npm install -g @anthropic-ai/claude-code (shim missing, package present)");
        var installOk = await TryRunNpmInstallGlobalAsync(logger, ct);
        actions.Add(installOk ? "npm install -g completed" : "npm install -g failed (smoke re-test below is verdict)");

        var versionAfter = ReadPackageVersion(wrapDir);
        var (retestOk, retestError) = await SmokeTestAsync(shim, ct);
        return new HealOutcome(retestOk, actions, retestOk ? null : retestError ?? smokeError)
        {
            Category = category,
            VersionBefore = versionBefore,
            VersionAfter = versionAfter,
            NpmInstallAttempted = true,
        };
    }

    private static async Task<(bool Ok, string? Error)> SmokeTestAsync(string shim, CancellationToken ct)
    {
        if (!File.Exists(shim))
        {
            return (false, $"shim '{shim}' still missing after repair pass");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = shim,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // AGT-2673 root-cause finding: a bare `claude --version` smoke test
            // can itself trigger the CLI's own auto-updater, which racing a
            // concurrent repair/spawn on Windows is the leading suspect for the
            // 2026-08-13 / 2026-08-18 shim-corruption incidents (version jumped
            // 2.1.231 -> 2.1.234 between sightings). The smoke test's only job
            // is to answer "does this shim run", not to let the CLI mutate its
            // own install mid-probe.
            psi.Environment["CLAUDE_CODE_DISABLE_AUTOUPDATER"] = "1";
            using var p = Process.Start(psi);
            if (p is null)
            {
                return (false, "failed to start smoke-test probe");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception __ex) { SilentCatch.Note(__ex, "NpmShimHealer: best effort"); /* best effort */ }
                return (false, "smoke-test probe timed out");
            }

            if (p.ExitCode != 0)
            {
                return (false, $"smoke-test probe exited {p.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"smoke-test probe error: {ex.Message}");
        }

        return (true, null);
    }

    private static string? ReadPackageVersion(string wrapDir)
    {
        try
        {
            var pkgJson = Path.Combine(wrapDir, "package.json");
            if (!File.Exists(pkgJson)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pkgJson));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "NpmShimHealer: version read is best-effort root-cause evidence, never a repair gate");
            return null;
        }
    }

    private static DateTime? ReadLastNpmInstallAttempt(string markerPath, ILogger logger)
    {
        try
        {
            if (!File.Exists(markerPath)) return null;
            var text = File.ReadAllText(markerPath).Trim();
            return DateTime.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed
                : null;
        }
        catch (Exception ex)
        {
            // A stale/corrupt marker must never permanently block the fallback -
            // treat as "no prior attempt" rather than fail closed.
            logger.LogWarning(ex, "Failed to read npm-install cooldown marker {Path}; treating as no prior attempt", markerPath);
            return null;
        }
    }

    private static void WriteLastNpmInstallAttempt(string markerPath, DateTime utcNow, ILogger logger)
    {
        try
        {
            File.WriteAllText(markerPath, utcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            // Best-effort: if the marker can't be written, the next call retries
            // npm install -g immediately rather than silently double-cooling-down.
            logger.LogWarning(ex, "Failed to write npm-install cooldown marker {Path}", markerPath);
        }
    }

    private static async Task<bool> TryRunNpmInstallGlobalAsync(ILogger logger, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "npm",
                ArgumentList = { "install", "-g", "@anthropic-ai/claude-code" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return false;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // npm resolves the registry and may re-download the platform
            // binary; allow the same generous budget as the local postinstall.
            cts.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception __ex) { SilentCatch.Note(__ex, "NpmShimHealer: best effort"); /* best effort */ }
                logger.LogWarning("npm install -g @anthropic-ai/claude-code timed out");
                return false;
            }

            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "npm install -g @anthropic-ai/claude-code failed to start");
            return false;
        }
    }

    private static DateTime SafeLastWriteTime(string path)
    {
        try { return new FileInfo(path).LastWriteTimeUtc; }
        catch { return DateTime.MinValue; }
    }

    private static async Task<bool> TryRunPostInstallAsync(
        string wrapDir,
        ILogger logger,
        CancellationToken ct)
    {
        var installScript = Path.Combine(wrapDir, "install.cjs");
        if (!File.Exists(installScript))
        {
            logger.LogWarning("install.cjs not found at {Path}; cannot run postinstall", installScript);
            return false;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "install.cjs",
                WorkingDirectory = wrapDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return false;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Postinstall copies a 254 MB binary; allow generous wall-clock budget.
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception __ex) { SilentCatch.Note(__ex, "NpmShimHealer: best effort"); /* best effort */ }
                logger.LogWarning("postinstall (node install.cjs) timed out");
                return false;
            }

            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "postinstall (node install.cjs) failed to start");
            return false;
        }
    }
}
