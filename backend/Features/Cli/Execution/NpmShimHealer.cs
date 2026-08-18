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
    string? Error,
    /// <summary>
    /// False only when the npm package itself
    /// (<c>node_modules/@anthropic-ai/claude-code</c>) is absent - a
    /// first-time install, not a repair candidate. True in every other
    /// case, including a successful or failed repair, so existing 3-arg
    /// call sites keep compiling and reporting "repair attempted" verdicts.
    /// </summary>
    bool PackagePresent = true);

/// <summary>
/// The shape of a broken <c>claude</c> npm-shim install, used to pick the
/// right repair action. Pure classification over booleans the caller has
/// already observed on disk - no I/O here, so
/// <see cref="NpmShimHealer.ClassifyShimState"/> is directly unit-testable
/// on any OS without touching a real npm bin directory.
/// </summary>
internal enum ClaudeShimRepairShape
{
    /// <summary>The top-level launcher (<c>claude.cmd</c> et al.) is present. Nothing to do.</summary>
    Healthy,
    /// <summary>
    /// Launcher still missing after steps 1-3 ran, while the package
    /// payload under <c>node_modules/@anthropic-ai/claude-code</c> is
    /// intact - either because npm's global bin link vanished outright
    /// (no orphan, no <c>.old.*</c> sibling) or because the orphan/stub
    /// restore itself failed. The 2026-08-18 recurrence (AGT-W39, second
    /// sighting) was the no-orphan case: only a full <c>npm install -g</c>
    /// re-link fixes this shape.
    /// </summary>
    MissingLauncherPackagePresent,
    /// <summary>No package directory at all. First-time install, not a repair.</summary>
    Uninstalled,
}

public static class NpmShimHealer
{
    private const string ReinstallPackageSpec = "@anthropic-ai/claude-code";

    // Guards the cooldown-check-then-reinstall-then-journal sequence so
    // concurrent task spawns within this process can't all race past
    // NpmReinstallJournal.IsInCooldown before any of them appends.
    private static readonly SemaphoreSlim ReinstallGate = new(1, 1);

    /// <summary>
    /// Pure decision: given what the caller observed on disk after steps
    /// 1-3 already ran, which repair shape applies.
    /// <paramref name="launcherPresent"/> is the top-level
    /// <c>npmBin/claude.cmd</c> (or platform equivalent) existing;
    /// <paramref name="packageDirPresent"/> is
    /// <c>npmBin/node_modules/@anthropic-ai/claude-code</c> existing.
    /// </summary>
    internal static ClaudeShimRepairShape ClassifyShimState(bool launcherPresent, bool packageDirPresent)
    {
        if (launcherPresent) return ClaudeShimRepairShape.Healthy;
        if (!packageDirPresent) return ClaudeShimRepairShape.Uninstalled;
        return ClaudeShimRepairShape.MissingLauncherPackagePresent;
    }

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

        // 4.5. Full npm reinstall when the top-level launcher (claude.cmd)
        //    is STILL missing after steps 1-3 found nothing to rename or
        //    restore. Observed shape (AGT-W39, 2nd sighting 2026-08-18):
        //    npm's global bin links vanish outright - no orphan, no
        //    .old.* sibling - most likely the CLI's own auto-update racing
        //    itself. The package payload survives, so `npm install -g`
        //    re-links the launcher from package.json's `bin` map without
        //    re-fetching the ~254 MB platform binary. Bounded to one
        //    attempt per rolling hour (NpmReinstallJournal) so a
        //    persistently broken installer cannot spin npm in a retry
        //    storm; every attempt is journaled with the CLI version
        //    before/after so the auto-update trigger stays provable.
        //    A missing package directory is a different, non-repairable
        //    shape (first-time install) and is reported as such via
        //    HealOutcome.PackagePresent rather than attempted.
        var shim = Path.Combine(npmBin, "claude.cmd");
        var shape = ClassifyShimState(
            launcherPresent: File.Exists(shim),
            packageDirPresent: Directory.Exists(wrapDir));

        if (shape == ClaudeShimRepairShape.Uninstalled)
        {
            return new HealOutcome(false, actions,
                $"claude-code is not installed under '{npmBin}' (no {wrapDir}); this is a first-time install, not a repair",
                PackagePresent: false);
        }

        if (shape == ClaudeShimRepairShape.MissingLauncherPackagePresent)
        {
            // Serializes the cooldown check + reinstall + journal append
            // within this process: EnsureCliHealthyAsync runs once per task
            // spawn, so several spawns hitting a broken shim at once would
            // otherwise all observe "not in cooldown" before any of them
            // appends, each firing its own concurrent `npm install -g` -
            // defeating the one-attempt-per-hour bound. Does not cover the
            // separate boot-time shell preflight (a different process), but
            // that runs once at boot, not concurrently with itself.
            await ReinstallGate.WaitAsync(ct);
            try
            {
                if (NpmReinstallJournal.IsInCooldown(npmBin, out var remaining))
                {
                    actions.Add(
                        $"launcher '{Path.GetFileName(shim)}' missing with package present; " +
                        $"reinstall skipped, cooldown active ({remaining.TotalMinutes:F0}m remaining)");
                }
                else
                {
                    // The launcher (shim) is by definition missing in this
                    // branch, so it cannot report its own "before" version.
                    // The package directory survives, so its package.json
                    // is the only source for the pre-repair version.
                    var versionBefore = TryReadPackageVersion(wrapDir);
                    var reinstallOk = await TryRunGlobalReinstallAsync(logger, npmBin, ct);
                    var versionAfter = await ProbeVersionAsync(shim, ct);
                    actions.Add(reinstallOk
                        ? $"launcher missing, package present; ran 'npm install -g {ReinstallPackageSpec}' " +
                          $"(version {versionBefore ?? "none"} -> {versionAfter ?? "unknown"})"
                        : $"launcher missing, package present; 'npm install -g {ReinstallPackageSpec}' failed " +
                          $"(version before: {versionBefore ?? "none"})");
                    NpmReinstallJournal.Append(npmBin, new NpmReinstallJournalEntry(
                        DateTime.UtcNow,
                        Trigger: "missing-launcher-package-present",
                        VersionBefore: versionBefore,
                        VersionAfter: versionAfter,
                        Outcome: reinstallOk ? "repaired" : "failed"));
                }
            }
            finally
            {
                ReinstallGate.Release();
            }
        }

        // 5. Smoke test. The shim is what the OS actually invokes via PATH;
        //    call it directly so we don't depend on PATH ordering.
        if (!File.Exists(shim))
        {
            return new HealOutcome(false, actions, $"shim '{shim}' still missing after repair pass");
        }

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
                return new HealOutcome(false, actions, "failed to start smoke-test probe");
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
                return new HealOutcome(false, actions, "smoke-test probe timed out");
            }

            if (p.ExitCode != 0)
            {
                return new HealOutcome(false, actions, $"smoke-test probe exited {p.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            return new HealOutcome(false, actions, $"smoke-test probe error: {ex.Message}");
        }

        return new HealOutcome(true, actions, null);
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

    /// <summary>
    /// Best-effort read of the <c>version</c> field from the package's own
    /// <c>package.json</c>. Used for the reinstall journal's "before"
    /// version: at the point this fires the launcher is by definition
    /// missing (that's why we're here), so it cannot be asked for its own
    /// version via <c>--version</c> the way <see cref="ProbeVersionAsync"/>
    /// does for the "after" reading - the package directory is the only
    /// surviving source. Returns null on any failure (missing file,
    /// malformed JSON, missing field).
    /// </summary>
    internal static string? TryReadPackageVersion(string wrapDir)
    {
        var packageJsonPath = Path.Combine(wrapDir, "package.json");
        if (!File.Exists(packageJsonPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            return doc.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "NpmShimHealer: package.json version read is diagnostic-only");
            return null;
        }
    }

    /// <summary>
    /// Best-effort <c>&lt;shim&gt; --version</c> probe used only to record the
    /// before/after version for the reinstall journal. Returns null on any
    /// failure (missing file, non-zero exit, timeout) - the journal entry is
    /// still written with a null version rather than blocking the repair.
    /// </summary>
    private static async Task<string?> ProbeVersionAsync(string shimPath, CancellationToken ct)
    {
        if (!File.Exists(shimPath)) return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = shimPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var stdout = await p.StandardOutput.ReadToEndAsync(cts.Token);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception __ex) { SilentCatch.Note(__ex, "NpmShimHealer: best effort"); /* best effort */ }
                return null;
            }
            if (p.ExitCode != 0) return null;
            return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "NpmShimHealer: version probe is diagnostic-only");
            return null;
        }
    }

    /// <summary>
    /// Full <c>npm install -g @anthropic-ai/claude-code</c> reinstall. Unlike
    /// <see cref="TryRunPostInstallAsync"/> (re-runs the wrapper's own
    /// postinstall in place) this re-links the global bin launchers via npm
    /// itself, which is the only thing that recreates a launcher npm deleted
    /// outright. Generous timeout: npm may need to resolve the registry even
    /// when no new bytes are downloaded.
    /// </summary>
    private static async Task<bool> TryRunGlobalReinstallAsync(ILogger logger, string npmBin, CancellationToken ct)
    {
        var npmExe = GenericCliExecutionService.ResolveExecutable(
            OperatingSystem.IsWindows() ? "npm.cmd" : "npm");
        var psi = new ProcessStartInfo
        {
            FileName = npmExe,
            WorkingDirectory = npmBin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add("-g");
        psi.ArgumentList.Add(ReinstallPackageSpec);

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // npm resolves the registry even when no new bytes are needed;
            // generous budget so a slow network doesn't read as "failed".
            cts.CancelAfter(TimeSpan.FromMinutes(3));
            // Drain both streams concurrently with the wait, not after it.
            // `npm install -g` routinely writes enough to stdout (package
            // tree, deprecation/funding notices) to fill the OS pipe buffer;
            // reading only stderr-on-failure (as originally written) left
            // stdout undrained, so npm would block writing to a full pipe
            // and the process would never exit on its own - every failure
            // would misreport as "timed out" after burning the full 3-minute
            // budget instead of the real, usually much faster, outcome.
            var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception __ex) { SilentCatch.Note(__ex, "NpmShimHealer: best effort"); /* best effort */ }
                logger.LogWarning("npm install -g {Package} timed out", ReinstallPackageSpec);
                return false;
            }

            if (p.ExitCode != 0)
            {
                var stderr = await stderrTask;
                try { await stdoutTask; } catch (Exception __ex) { SilentCatch.Note(__ex, "NpmShimHealer: stdout drain is diagnostic-only"); }
                logger.LogWarning("npm install -g {Package} exited {ExitCode}: {Stderr}",
                    ReinstallPackageSpec, p.ExitCode, stderr.Trim());
                return false;
            }
            try { await stdoutTask; } catch (Exception __ex) { SilentCatch.Note(__ex, "NpmShimHealer: stdout drain is diagnostic-only"); }
            try { await stderrTask; } catch (Exception __ex) { SilentCatch.Note(__ex, "NpmShimHealer: stderr drain is diagnostic-only"); }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "npm install -g {Package} failed to start", ReinstallPackageSpec);
            return false;
        }
    }
}

/// <summary>
/// One entry in the reinstall root-cause journal
/// (<c>&lt;npmBin&gt;/.atp-npm-reinstall-journal.jsonl</c>): what triggered a
/// full <c>npm install -g</c> repair, the CLI version immediately before and
/// after, and the outcome. Shared file, append-only, so both
/// <c>tools/check-cli-shims.sh</c> (boot-time preflight) and this in-process
/// healer observe and respect the same cooldown.
/// </summary>
internal sealed record NpmReinstallJournalEntry(
    DateTime Ts,
    string Trigger,
    string? VersionBefore,
    string? VersionAfter,
    string Outcome);

/// <summary>
/// Bounds the full <c>npm install -g</c> reinstall to one attempt per
/// rolling hour and records each attempt for root-cause analysis. A
/// persistently broken installer (e.g. a locked file, a dead registry) must
/// not turn every pre-spawn health check into a fresh multi-minute npm
/// invocation.
/// </summary>
internal static class NpmReinstallJournal
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(1);

    // camelCase to match the sibling shell implementation in
    // tools/check-cli-shims.sh (which writes plain "ts"/"trigger"/...
    // keys by hand) - both tools append to and read the same file, so a
    // casing mismatch would silently break the shared cooldown rather than
    // throw. PropertyNameCaseInsensitive on read is defense in depth.
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static string JournalPath(string npmBin) =>
        Path.Combine(npmBin, ".atp-npm-reinstall-journal.jsonl");

    /// <summary>
    /// True when the last journaled attempt is still within the cooldown
    /// window. <paramref name="remaining"/> is the time left in that window
    /// (zero when not in cooldown). Best-effort: a corrupt or unreadable
    /// journal is treated as "not in cooldown" so a bad journal file cannot
    /// permanently block repair. Thin I/O coordinator over the pure
    /// <see cref="IsInCooldown(DateTime?, DateTime, out TimeSpan)"/> decision
    /// (dotnet-backend style guide: "pure policy first" - filesystem read
    /// and clock access stay here, the branching stays testable without
    /// either).
    /// </summary>
    public static bool IsInCooldown(string npmBin, out TimeSpan remaining) =>
        IsInCooldown(TryReadLast(npmBin)?.Ts, DateTime.UtcNow, out remaining);

    /// <summary>
    /// Pure decision: given the last attempt's timestamp (or null for no
    /// history) and the current time, is a new attempt still in cooldown.
    /// No I/O, no clock read - directly unit-testable with fixed inputs.
    /// </summary>
    internal static bool IsInCooldown(DateTime? lastAttemptUtc, DateTime nowUtc, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (lastAttemptUtc is null) return false;

        var elapsed = nowUtc - lastAttemptUtc.Value;
        if (elapsed >= Cooldown) return false;

        remaining = Cooldown - elapsed;
        return true;
    }

    /// <summary>
    /// Append one entry. Best-effort: a write failure is swallowed so a
    /// read-only or missing directory never blocks the repair pass itself.
    /// </summary>
    public static void Append(string npmBin, NpmReinstallJournalEntry entry)
    {
        try
        {
            var path = JournalPath(npmBin);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(entry, WriteOpts) + Environment.NewLine;
            File.AppendAllText(path, line);
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "NpmReinstallJournal: best-effort append; cooldown degrades gracefully");
        }
    }

    private static NpmReinstallJournalEntry? TryReadLast(string npmBin)
    {
        var path = JournalPath(npmBin);
        if (!File.Exists(path)) return null;
        try
        {
            NpmReinstallJournalEntry? last = null;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var parsed = JsonSerializer.Deserialize<NpmReinstallJournalEntry>(line, ReadOpts);
                    if (parsed is not null) last = parsed;
                }
                catch (JsonException __ex)
                {
                    SilentCatch.Note(__ex, "NpmReinstallJournal: skip torn/malformed line");
                }
            }
            return last;
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "NpmReinstallJournal: unreadable journal treated as no-history");
            return null;
        }
    }
}
