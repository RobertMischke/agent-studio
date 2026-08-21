using System.Diagnostics;
using System.Text.Json;

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
    /// <summary>Package version read from <c>package.json</c> on disk before this repair
    /// pass ran (survives a missing/broken shim - npm does not delete package.json when
    /// only the bin-links disappear). Null when the package directory itself is absent.</summary>
    public string? VersionBefore { get; init; }

    /// <summary>Version reported by the post-repair <c>--version</c> smoke test. Null when
    /// the smoke test never ran (e.g. still broken) or failed.</summary>
    public string? VersionAfter { get; init; }

    /// <summary>True when this pass ran <c>npm install -g @anthropic-ai/claude-code</c>.</summary>
    public bool NpmInstallAttempted { get; init; }

    /// <summary>True when an <c>npm install -g</c> was indicated but skipped because the
    /// last attempt is still within <see cref="NpmShimHealer.NpmInstallCooldown"/>.</summary>
    public bool NpmInstallThrottled { get; init; }

    /// <summary>Classification of what this pass found. One of <see cref="NpmShimHealDiagnosis"/>.</summary>
    public string Diagnosis { get; init; } = NpmShimHealDiagnosis.Healthy;
}

/// <summary>Classification constants for <see cref="HealOutcome.Diagnosis"/>.</summary>
public static class NpmShimHealDiagnosis
{
    /// <summary>Nothing was wrong; the pre-existing <c>--version</c> probe already succeeded.</summary>
    public const string Healthy = "healthy";
    /// <summary>Orphan-shim rename, platform-binary restore, or postinstall repaired the
    /// install without needing a fresh <c>npm install -g</c>.</summary>
    public const string RepairedWithoutReinstall = "repaired-without-reinstall";
    /// <summary>npm's own bin-links (<c>claude</c> / <c>claude.cmd</c> / <c>claude.ps1</c>)
    /// are gone with no orphan artifact to rename back, but the package directory is still
    /// present under <c>node_modules/@anthropic-ai/claude-code</c> - the AGT-2673 shape.</summary>
    public const string ShimMissingPackagePresent = "shim-missing-package-present";
    /// <summary>The package directory itself is absent. This looks like a deliberate
    /// uninstall rather than breakage, so no automatic <c>npm install -g</c> is attempted.</summary>
    public const string TrulyUninstalled = "truly-uninstalled";
    /// <summary>The host environment (APPDATA, npm global bin) could not be resolved at all.</summary>
    public const string EnvironmentUnavailable = "environment-unavailable";
}

public static class NpmShimHealer
{
    /// <summary>Minimum interval between automatic <c>npm install -g</c> repair attempts
    /// for the same process lifetime. In-memory only - a backend restart resets it, matching
    /// the wider self-heal convention (see <c>CrossSlugInfraCircuitBreaker</c>). Bounds the
    /// cost/noise of repeatedly reinstalling a ~254 MB package against a host stuck in a
    /// genuinely broken state.</summary>
    public static readonly TimeSpan NpmInstallCooldown = TimeSpan.FromHours(1);

    private static readonly object ThrottleLock = new();
    private static DateTime? _lastNpmInstallAttemptUtc;

    /// <summary>Test seam: clear the in-memory throttle so a test can force the next call
    /// to attempt <c>npm install -g</c> regardless of prior calls in the same process.</summary>
    internal static void ResetNpmInstallThrottleForTests()
    {
        lock (ThrottleLock) { _lastNpmInstallAttemptUtc = null; }
    }

    /// <summary>Test seam: read the in-memory last-attempt timestamp.</summary>
    internal static DateTime? LastNpmInstallAttemptUtcForTests()
    {
        lock (ThrottleLock) { return _lastNpmInstallAttemptUtc; }
    }

    private static bool TryEnterNpmInstallThrottle(DateTime utcNow)
    {
        lock (ThrottleLock)
        {
            if (_lastNpmInstallAttemptUtc.HasValue && utcNow - _lastNpmInstallAttemptUtc.Value < NpmInstallCooldown)
                return false;
            _lastNpmInstallAttemptUtc = utcNow;
            return true;
        }
    }

    /// <summary>
    /// Repair the <c>claude</c> npm-shim install on Windows and smoke-test
    /// the resulting <c>claude.cmd</c>. No-op on non-Windows hosts (the
    /// failure mode is Windows-specific).
    /// </summary>
    public static Task<HealOutcome> TryHealClaudeAsync(
        ILogger logger,
        CancellationToken ct,
        string? workspaceRoot = null,
        DateTime? utcNow = null,
        IJsonlAppender? appender = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new HealOutcome(true, Array.Empty<string>(), null));
        }

        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrEmpty(appData))
        {
            var now = utcNow ?? DateTime.UtcNow;
            var envOutcome = new HealOutcome(false, Array.Empty<string>(),
                "APPDATA env var is unset; cannot locate npm global bin")
            { Diagnosis = NpmShimHealDiagnosis.EnvironmentUnavailable };
            NpmShimRepairLog.Append(workspaceRoot, "claude", envOutcome, now, logger, appender);
            return Task.FromResult(envOutcome);
        }

        return HealAtAsync(Path.Combine(appData, "npm"), logger, ct, workspaceRoot, utcNow, appender);
    }

    /// <summary>
    /// OS-independent repair core, factored out of <see cref="TryHealClaudeAsync"/> so the
    /// shim-detection and diagnosis logic is unit-testable against a synthetic
    /// <paramref name="npmBin"/> directory tree on any platform (AGT-2673 requirement: tests
    /// where portable). The public entry point is still the only one gated on
    /// <see cref="OperatingSystem.IsWindows"/> and real <c>APPDATA</c> resolution - the
    /// underlying file operations here have no actual Windows-API dependency, only the
    /// npm-shim failure mode itself is Windows-specific.
    /// </summary>
    internal static async Task<HealOutcome> HealAtAsync(
        string npmBin,
        ILogger logger,
        CancellationToken ct,
        string? workspaceRoot = null,
        DateTime? utcNow = null,
        IJsonlAppender? appender = null,
        Func<ILogger, CancellationToken, Task<(bool Ok, string? Output, string? Error)>>? npmInstallRunner = null)
    {
        var now = utcNow ?? DateTime.UtcNow;

        if (!Directory.Exists(npmBin))
        {
            var envOutcome = new HealOutcome(false, Array.Empty<string>(),
                $"npm global bin not found at '{npmBin}'")
            { Diagnosis = NpmShimHealDiagnosis.EnvironmentUnavailable };
            NpmShimRepairLog.Append(workspaceRoot, "claude", envOutcome, now, logger, appender);
            return envOutcome;
        }

        var wrapDirForVersion = Path.Combine(npmBin, "node_modules", "@anthropic-ai", "claude-code");
        var versionBefore = ReadPackageVersion(wrapDirForVersion);

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
        var wrapDir = wrapDirForVersion;
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

        var diagnosis = actions.Count > 0 ? NpmShimHealDiagnosis.RepairedWithoutReinstall : NpmShimHealDiagnosis.Healthy;
        var npmInstallAttempted = false;
        var npmInstallThrottled = false;

        // 4.5. npm's own bin-links (claude / claude.cmd / claude.ps1) are gone
        //      and steps 1-4 found nothing to rename or restore them from -
        //      the AGT-2673 shape (2nd occurrence 2026-08-18): the package
        //      remains under node_modules/@anthropic-ai/claude-code but npm's
        //      bin-linking itself never ran or was wiped, most likely by a
        //      racing auto-update or an interrupted npm postinstall. The prior
        //      steps cannot repair this - there is no orphan artifact to
        //      recover from. Distinguish it from a deliberate uninstall
        //      (package directory itself absent, see TrulyUninstalled) before
        //      reaching for `npm install -g`, and bound the reinstall attempt
        //      to once per NpmInstallCooldown so a persistently broken host
        //      does not reinstall the ~254 MB package on every spawn.
        var shim = Path.Combine(npmBin, "claude.cmd");
        if (!File.Exists(shim))
        {
            if (!Directory.Exists(wrapDir))
            {
                diagnosis = NpmShimHealDiagnosis.TrulyUninstalled;
                var outcome = new HealOutcome(false, actions,
                    $"npm shim '{shim}' missing and package directory '{wrapDir}' not found; " +
                    "looks uninstalled, not merely broken - no automatic repair attempted")
                {
                    VersionBefore = versionBefore,
                    Diagnosis = diagnosis,
                };
                NpmShimRepairLog.Append(workspaceRoot, "claude", outcome, now, logger, appender);
                return outcome;
            }

            diagnosis = NpmShimHealDiagnosis.ShimMissingPackagePresent;
            if (!TryEnterNpmInstallThrottle(now))
            {
                npmInstallThrottled = true;
                var outcome = new HealOutcome(false, actions,
                    $"npm shim '{shim}' missing; package present but an npm install -g repair " +
                    $"already ran within the last {NpmInstallCooldown.TotalMinutes:0} minutes - throttled")
                {
                    VersionBefore = versionBefore,
                    Diagnosis = diagnosis,
                    NpmInstallThrottled = true,
                };
                NpmShimRepairLog.Append(workspaceRoot, "claude", outcome, now, logger, appender);
                return outcome;
            }

            npmInstallAttempted = true;
            actions.Add("npm bin-links missing with package present, running npm install -g @anthropic-ai/claude-code");
            var runner = npmInstallRunner ?? TryRunNpmInstallGlobalAsync;
            var (installOk, _, installError) = await runner(logger, ct);
            actions.Add(installOk ? "npm install -g completed" : $"npm install -g failed: {installError}");

            if (!installOk)
            {
                var outcome = new HealOutcome(false, actions, installError)
                {
                    VersionBefore = versionBefore,
                    Diagnosis = diagnosis,
                    NpmInstallAttempted = true,
                };
                NpmShimRepairLog.Append(workspaceRoot, "claude", outcome, now, logger, appender);
                return outcome;
            }

            if (!File.Exists(shim))
            {
                var outcome = new HealOutcome(false, actions,
                    $"shim '{shim}' still missing after npm install -g")
                {
                    VersionBefore = versionBefore,
                    Diagnosis = diagnosis,
                    NpmInstallAttempted = true,
                };
                NpmShimRepairLog.Append(workspaceRoot, "claude", outcome, now, logger, appender);
                return outcome;
            }
        }

        // 5. Smoke test. The shim is what the OS actually invokes via PATH;
        //    call it directly so we don't depend on PATH ordering.
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = shim,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null)
            {
                var outcome = new HealOutcome(false, actions, "failed to start smoke-test probe")
                { VersionBefore = versionBefore, Diagnosis = diagnosis, NpmInstallAttempted = npmInstallAttempted, NpmInstallThrottled = npmInstallThrottled };
                NpmShimRepairLog.Append(workspaceRoot, "claude", outcome, now, logger, appender);
                return outcome;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            string smokeOutput = "";
            try
            {
                smokeOutput = await p.StandardOutput.ReadToEndAsync(cts.Token);
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception __ex) { SilentCatch.Note(__ex, "NpmShimHealer: best effort"); /* best effort */ }
                var outcome = new HealOutcome(false, actions, "smoke-test probe timed out")
                { VersionBefore = versionBefore, Diagnosis = diagnosis, NpmInstallAttempted = npmInstallAttempted, NpmInstallThrottled = npmInstallThrottled };
                NpmShimRepairLog.Append(workspaceRoot, "claude", outcome, now, logger, appender);
                return outcome;
            }

            if (p.ExitCode != 0)
            {
                var outcome = new HealOutcome(false, actions, $"smoke-test probe exited {p.ExitCode}")
                { VersionBefore = versionBefore, Diagnosis = diagnosis, NpmInstallAttempted = npmInstallAttempted, NpmInstallThrottled = npmInstallThrottled };
                NpmShimRepairLog.Append(workspaceRoot, "claude", outcome, now, logger, appender);
                return outcome;
            }

            var versionAfter = ExtractFirstLine(smokeOutput);
            var success = new HealOutcome(true, actions, null)
            {
                VersionBefore = versionBefore,
                VersionAfter = versionAfter,
                Diagnosis = diagnosis,
                NpmInstallAttempted = npmInstallAttempted,
                NpmInstallThrottled = npmInstallThrottled,
            };
            NpmShimRepairLog.Append(workspaceRoot, "claude", success, now, logger, appender);
            return success;
        }
        catch (Exception ex)
        {
            var outcome = new HealOutcome(false, actions, $"smoke-test probe error: {ex.Message}")
            { VersionBefore = versionBefore, Diagnosis = diagnosis, NpmInstallAttempted = npmInstallAttempted, NpmInstallThrottled = npmInstallThrottled };
            NpmShimRepairLog.Append(workspaceRoot, "claude", outcome, now, logger, appender);
            return outcome;
        }
    }

    private static string? ExtractFirstLine(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? null
            : raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();

    private static string? ReadPackageVersion(string wrapDir)
    {
        try
        {
            var pkgPath = Path.Combine(wrapDir, "package.json");
            if (!File.Exists(pkgPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(pkgPath));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "NpmShimHealer: package.json version read is best-effort"); return null; }
    }

    /// <summary>Regenerate npm's global bin-links for the claude-code package. Runs only when
    /// the package directory is present but npm's own <c>claude</c>/<c>claude.cmd</c>/<c>claude.ps1</c>
    /// links are gone with no orphan to rename back (see <see cref="NpmShimHealDiagnosis.ShimMissingPackagePresent"/>).</summary>
    private static async Task<(bool Ok, string? Output, string? Error)> TryRunNpmInstallGlobalAsync(
        ILogger logger, CancellationToken ct)
    {
        var npmExe = GenericCliExecutionService.ResolveExecutable("npm");
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = npmExe,
                ArgumentList = { "install", "-g", "@anthropic-ai/claude-code" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return (false, null, "failed to start npm install -g process");

            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Reinstalling the ~254 MB package over the network needs a generous budget.
            cts.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception __ex) { SilentCatch.Note(__ex, "NpmShimHealer: best effort"); /* best effort */ }
                logger.LogWarning("npm install -g @anthropic-ai/claude-code timed out after 3 minutes");
                return (false, null, "npm install -g timed out after 3 minutes");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (p.ExitCode != 0)
            {
                var truncatedErr = stderr.Length > 500 ? stderr[..500] : stderr;
                return (false, stdout, $"npm install -g exited {p.ExitCode}: {truncatedErr}");
            }
            return (true, stdout, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "npm install -g @anthropic-ai/claude-code failed to start");
            return (false, null, $"npm install -g failed to start: {ex.Message}");
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
