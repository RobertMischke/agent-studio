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
    string? Error);

public static class NpmShimHealer
{
    /// <summary>
    /// Re-runs a known global npm package install through the existing shim
    /// repair process boundary. Callers remain responsible for package
    /// allowlisting and attempt throttling.
    /// </summary>
    internal static async Task<(int? ExitCode, string Output)> InstallGlobalPackageAsync(
        string packageName,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = GenericCliExecutionService.ResolveExecutable("npm"),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("install");
            process.StartInfo.ArgumentList.Add("-g");
            process.StartInfo.ArgumentList.Add(packageName);
            if (!process.Start()) return (null, "npm did not start");

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(timeout);
            try { await process.WaitForExitAsync(bounded.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "NpmShimHealer: best-effort npm timeout cleanup"); }
                return (null, $"npm install timed out after {timeout.TotalMinutes:0} minutes");
            }
            return (process.ExitCode, $"{await stdout}\n{await stderr}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
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

        // 5. Smoke test. The shim is what the OS actually invokes via PATH;
        //    call it directly so we don't depend on PATH ordering.
        var shim = Path.Combine(npmBin, "claude.cmd");
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
}
