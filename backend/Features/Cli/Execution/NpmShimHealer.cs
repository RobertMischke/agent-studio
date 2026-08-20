using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

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
///
/// <para>
/// A distinct failure shape (AGT-2673): npm's own auto-update can delete the
/// bin shims outright, with the <c>@anthropic-ai/claude-code</c> package
/// still on disk and no orphan for steps 1-4 to restore from. That shape
/// gets a bounded <c>npm install -g</c> reinstall (<see cref="ClaudeReinstallPolicy"/>
/// gates it to once an hour and only when the package is actually present -
/// a truly uninstalled host is a different, deliberately unhandled
/// situation). Every attempt is journaled to
/// <see cref="ClaudeRepairJournalStore"/> (colocated with the npm bin) with
/// the CLI version before/after, and every outcome - repaired or still
/// failing - is logged, never silent.
/// </para>
/// </summary>
public sealed record HealOutcome(
    bool Available,
    IReadOnlyList<string> Actions,
    string? Error);

public static class NpmShimHealer
{
    /// <summary>
    /// Serialises the full-reinstall check-then-act sequence (read last
    /// attempt, decide, spawn npm, record). <see cref="TryHealClaudeAsync"/>
    /// runs once per spawn and several spawns can be in flight together, so
    /// without this gate two concurrent callers could both observe "no
    /// recent attempt" and both fire <c>npm install -g</c> at once.
    /// </summary>
    private static readonly SemaphoreSlim ReinstallGate = new(1, 1);

    private const string ClaudeNpmPackage = "@anthropic-ai/claude-code";

    /// <summary>Matches the existing postinstall budget: same download+unpack shape, same generous ceiling.</summary>
    private static readonly TimeSpan PostInstallTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReinstallTimeout = TimeSpan.FromMinutes(2);

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

        // 5. Last resort: shim entirely gone, no orphan to restore (steps 1-4
        //    had nothing to act on) - the shape observed twice on the Windows
        //    control-plane host (docs/operations/live-improvement-log/index.html,
        //    second sighting: "npm bin shims gone, package present; version
        //    jumped 2.1.231->2.1.234 - auto-update suspected"). Distinguish
        //    missing-shim-with-package-present from a truly uninstalled host:
        //    only the former gets an automatic `npm install -g` reinstall,
        //    bounded to once an hour so a still-broken installer is not retried
        //    on every spawn.
        var shim = Path.Combine(npmBin, "claude.cmd");
        if (!File.Exists(shim))
        {
            var reinstallAction = await TryFullReinstallAsync(npmBin, wrapDir, logger, ct);
            if (reinstallAction is not null) actions.Add(reinstallAction);
        }

        // 6. Smoke test. The shim is what the OS actually invokes via PATH;
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
            var result = await RunCapturedAsync(
                "node", ["install.cjs"], wrapDir, PostInstallTimeout, logger,
                "postinstall (node install.cjs)", ct);
            return result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "postinstall (node install.cjs) failed to start");
            return false;
        }
    }

    /// <summary>
    /// Steps 1-4's shape assumptions (an orphan to rename, an <c>.old.*</c>
    /// sibling, a stub binary) all require something on disk to restore from.
    /// npm's own auto-update silently deleting the bin shims outright leaves
    /// none of those - the only remaining repair is a full reinstall. Returns
    /// a human-readable action line (success or failure, always prefixed so a
    /// log reader does not have to open the journal to know what happened),
    /// or <c>null</c> when nothing was attempted (shim missing but no package
    /// to reinstall from, inside the cooldown window, or the caller cancelled
    /// while waiting for the reinstall lock - all self-explanatory from the
    /// caller's perspective and not worth an action line on every spawn).
    /// </summary>
    private static async Task<string?> TryFullReinstallAsync(
        string npmBin,
        string wrapDir,
        ILogger logger,
        CancellationToken ct)
    {
        var packageJsonPath = Path.Combine(wrapDir, "package.json");
        var packagePresent = File.Exists(packageJsonPath);

        try
        {
            await ReinstallGate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Another caller's reinstall (or postinstall/smoke-test pass) is
            // in flight and this caller's own budget ran out first - the
            // in-flight attempt still completes and journals itself, so
            // there is nothing this caller needs to do beyond giving up its
            // own turn gracefully instead of throwing out of a method whose
            // contract (like every other path in this class) is "always
            // returns, never throws".
            return null;
        }
        try
        {
            var lastAttemptAt = ClaudeRepairJournalStore.TryReadLastAttemptAt(npmBin, logger);
            var decision = ClaudeReinstallPolicy.Decide(
                packagePresent: packagePresent,
                lastAttemptAt: lastAttemptAt,
                now: DateTimeOffset.UtcNow);

            switch (decision)
            {
                case ClaudeReinstallDecision.TrulyUninstalled:
                    logger.LogWarning(
                        "claude shim missing and no {Package} package found at {Path}; not auto-provisioning a fresh install",
                        ClaudeNpmPackage, packageJsonPath);
                    return null;
                case ClaudeReinstallDecision.CooldownActive:
                    logger.LogInformation(
                        "claude shim missing but a full reinstall already ran at {LastAttempt:o} "
                        + "(within the {CooldownMinutes:0}m cooldown); skipping to avoid hammering npm",
                        lastAttemptAt, ClaudeReinstallPolicy.Cooldown.TotalMinutes);
                    return null;
            }

            // Read only on the attempt path: every other decision discards
            // this value, and a broken install can be re-probed on every
            // spawn while the cooldown blocks the actual reinstall.
            var versionBefore = ReadPackageVersion(packageJsonPath, logger);
            var attemptedAt = DateTimeOffset.UtcNow;
            var npmCommand = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
            (int ExitCode, string StdOut, string StdErr) result;
            try
            {
                result = await RunCapturedAsync(
                    npmCommand, ["install", "-g", ClaudeNpmPackage], npmBin, ReinstallTimeout, logger,
                    $"{npmCommand} install -g {ClaudeNpmPackage}", ct);
            }
            catch (Exception ex)
            {
                var startFailure = $"ALARM: claude full reinstall could not be started: {ex.Message}";
                ClaudeRepairJournalStore.Append(
                    npmBin,
                    new ClaudeRepairJournalEntry(attemptedAt, false, versionBefore, null, startFailure),
                    logger);
                logger.LogError(ex, "npm install -g {Package} failed to start", ClaudeNpmPackage);
                return startFailure;
            }

            var versionAfter = ReadPackageVersion(packageJsonPath, logger);
            // npm's own atomic-rename race (the reason this class exists) can
            // leave claude.cmd invisible for a brief window right after a
            // genuinely successful install exits; give it a few short
            // retries before recording a false failure.
            var succeeded = result.ExitCode == 0
                             && await ShimAppearsAsync(Path.Combine(npmBin, "claude.cmd"));
            var detail = succeeded
                ? $"CLI repaired at {attemptedAt:O} via npm install -g reinstall "
                  + $"(version {versionBefore ?? "unknown"} -> {versionAfter ?? "unknown"})"
                : $"ALARM: claude full reinstall failed (npm exit {result.ExitCode}, "
                  + $"version before {versionBefore ?? "unknown"}): {Excerpt(result.StdErr, result.StdOut)}";

            ClaudeRepairJournalStore.Append(
                npmBin,
                new ClaudeRepairJournalEntry(attemptedAt, succeeded, versionBefore, versionAfter, detail),
                logger);

            if (succeeded) logger.LogInformation("{Detail}", detail);
            else logger.LogError("{Detail}", detail);

            return detail;
        }
        finally
        {
            ReinstallGate.Release();
        }
    }

    private static async Task<bool> ShimAppearsAsync(string shimPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (File.Exists(shimPath)) return true;
            if (attempt >= 4) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
    }

    /// <summary>Retained output is capped: only a short <see cref="Excerpt"/> of it is ever used.</summary>
    private const int OutputCaptureBudgetChars = 8_000;

    /// <summary>
    /// Shared spawn/drain/timeout-kill scaffolding for the two child processes
    /// this class runs beyond the smoke test (postinstall, full reinstall).
    /// Both can print far more than a pipe buffer holds; draining both
    /// streams via the async event API (same pattern as
    /// runner/ProcessRunner.cs) is what keeps the child from blocking on its
    /// own output, which is what would otherwise make <c>WaitForExitAsync</c>
    /// hang until the timeout regardless of whether the child actually
    /// finished.
    /// </summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCapturedAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan timeout,
        ILogger logger,
        string diagnosticLabel,
        CancellationToken ct)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        if (workingDirectory is not null) p.StartInfo.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) p.StartInfo.ArgumentList.Add(argument);

        var stdOut = new System.Text.StringBuilder();
        var stdErr = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null && stdOut.Length < OutputCaptureBudgetChars) stdOut.AppendLine(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null && stdErr.Length < OutputCaptureBudgetChars) stdErr.AppendLine(e.Data);
        };

        if (!p.Start())
            throw new InvalidOperationException($"failed to start '{diagnosticLabel}'");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception ex) { SilentCatch.Note(ex, "NpmShimHealer: kill after timeout"); }
            logger.LogWarning("{Label} timed out after {Timeout}", diagnosticLabel, timeout);
            stdErr.AppendLine($"[runner] {diagnosticLabel} timed out");
            return (-1, stdOut.ToString(), stdErr.ToString());
        }

        return (p.ExitCode, stdOut.ToString(), stdErr.ToString());
    }

    private static string? ReadPackageVersion(string packageJsonPath, ILogger logger)
    {
        if (!File.Exists(packageJsonPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            return doc.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read version from {Path}", packageJsonPath);
            return null;
        }
    }

    /// <summary>Anything token-shaped is stripped before the excerpt reaches the journal or the log.</summary>
    private static readonly Regex SecretShaped = new(
        @"\b(?:sk-[A-Za-z0-9_\-]{6,}|[A-Za-z0-9_\-]{40,})\b",
        RegexOptions.Compiled);

    private static string Excerpt(string stdErr, string stdOut)
    {
        var text = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        var redacted = SecretShaped.Replace(single, "[redacted]");
        return redacted.Length <= 300 ? redacted : redacted[..300] + "...";
    }
}
