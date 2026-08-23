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
/// <param name="Available">Smoke-test verdict after the repair pass.</param>
/// <param name="Actions">Repair steps actually taken, in order.</param>
/// <param name="Error">Failure reason when <paramref name="Available"/> is false.</param>
/// <param name="PackagePresent">
/// Whether the <c>@anthropic-ai/claude-code</c> package directory existed at
/// all. False means there is nothing on disk this healer could repair (a
/// truly-uninstalled CLI, not a half-installed shim) - callers should not
/// treat a false-with-no-actions outcome the same as a failed repair attempt.
/// </param>
/// <param name="VersionBefore">
/// Installed package.json version read from disk before any repair step
/// runs. Read from disk, not via <c>claude --version</c>, because the whole
/// reason this heal path fires is that the executable is currently broken -
/// probing through the broken binary would always observe null here.
/// </param>
/// <param name="VersionAfter">Same read, taken after the repair pass.</param>
public sealed record HealOutcome(
    bool Available,
    IReadOnlyList<string> Actions,
    string? Error,
    bool PackagePresent = false,
    string? VersionBefore = null,
    string? VersionAfter = null);

public static class NpmShimHealer
{
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

        // Resolved up front and read from disk (package.json), not via
        // `claude --version`, so the "before" value is captured while it can
        // still be non-null: by construction, the executable is already
        // known-broken on every call path that reaches this method, so a
        // probe through the binary itself would always observe absence here.
        var wrapDir = Path.Combine(npmBin, "node_modules", "@anthropic-ai", "claude-code");
        var packagePresent = Directory.Exists(wrapDir);
        var versionBefore = ReadInstalledVersion(wrapDir, logger);

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

        // Read again after the repair pass so the pair proves (or disproves)
        // that a version change - not just a shim rename - happened between
        // the two reads. Same disk read, not the smoke-tested binary below.
        var versionAfter = ReadInstalledVersion(wrapDir, logger);

        // 5. Smoke test. The shim is what the OS actually invokes via PATH;
        //    call it directly so we don't depend on PATH ordering.
        var shim = Path.Combine(npmBin, "claude.cmd");
        if (!File.Exists(shim))
        {
            return new HealOutcome(false, actions, $"shim '{shim}' still missing after repair pass",
                packagePresent, versionBefore, versionAfter);
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
                return new HealOutcome(false, actions, "failed to start smoke-test probe",
                    packagePresent, versionBefore, versionAfter);
            }

            // Drain both streams concurrently with the wait: `claude --version`
            // output is small, but an unread redirected pipe is a latent
            // deadlock risk (child blocks on a full OS pipe buffer, parent
            // blocks in WaitForExitAsync) that only needs a noisier CLI
            // version banner to trigger - not worth leaving unread.
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception __ex) { SilentCatch.Note(__ex, "NpmShimHealer: best effort"); /* best effort */ }
                // Drain before the `using` disposes p below: an abandoned
                // ReadToEndAsync racing Dispose() throws ObjectDisposedException
                // in a task nobody observes.
                await DrainBothAsync(stdoutTask, stderrTask, logger);
                return new HealOutcome(false, actions, "smoke-test probe timed out",
                    packagePresent, versionBefore, versionAfter);
            }

            if (p.ExitCode != 0)
            {
                await DrainBothAsync(stdoutTask, stderrTask, logger);
                return new HealOutcome(false, actions, $"smoke-test probe exited {p.ExitCode}",
                    packagePresent, versionBefore, versionAfter);
            }

            await DrainBothAsync(stdoutTask, stderrTask, logger);
        }
        catch (Exception ex)
        {
            return new HealOutcome(false, actions, $"smoke-test probe error: {ex.Message}",
                packagePresent, versionBefore, versionAfter);
        }

        return new HealOutcome(true, actions, null, packagePresent, versionBefore, versionAfter);
    }

    /// <summary>
    /// Reads the installed package version straight off <c>package.json</c>
    /// in the claude-code wrapper directory. Works whether or not the CLI
    /// binary itself currently runs - the only signal that lets a caller
    /// prove "the version changed under us" rather than "the shim broke".
    /// </summary>
    /// <summary>Internal test seam - the read itself has no Windows dependency, unlike the rest of this class.</summary>
    internal static string? ReadInstalledVersion(string wrapDir, ILogger logger)
    {
        var pkgJsonPath = Path.Combine(wrapDir, "package.json");
        if (!File.Exists(pkgJsonPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pkgJsonPath));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read installed version from {Path}", pkgJsonPath);
            return null;
        }
    }

    private static DateTime SafeLastWriteTime(string path)
    {
        try { return new FileInfo(path).LastWriteTimeUtc; }
        catch { return DateTime.MinValue; }
    }

    private static async Task<string> SafeReadAsync(Task<string> readTask, ILogger logger)
    {
        try { return await readTask; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to drain postinstall output stream");
            return "";
        }
    }

    /// <summary>
    /// Drains both redirected streams before the caller's <c>using</c>
    /// disposes the process, on every return path - not just the success
    /// path. An abandoned <c>ReadToEndAsync</c> racing <c>Process.Dispose()</c>
    /// throws <see cref="ObjectDisposedException"/> inside a task nobody
    /// observes otherwise.
    /// </summary>
    private static async Task DrainBothAsync(Task<string> stdoutTask, Task<string> stderrTask, ILogger logger)
    {
        await SafeReadAsync(stdoutTask, logger);
        await SafeReadAsync(stderrTask, logger);
    }

    private static string Truncate(string s) => s.Length <= 2000 ? s : s[^2000..];

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

            // Must be read concurrently with the wait, not after. install.cjs
            // logs npm progress while it copies a 254 MB binary - enough
            // output to fill the OS pipe buffer. An unread redirected stream
            // then blocks the child on write() while WaitForExitAsync blocks
            // the parent on the exit that write() is blocking, a deadlock
            // that only resolves via the 2-minute timeout kill below instead
            // of the postinstall's real, much shorter completion time.
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);

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
                // Drain before the `using` disposes p below (same reasoning
                // as the smoke test's DrainBothAsync calls).
                await DrainBothAsync(stdoutTask, stderrTask, logger);
                logger.LogWarning("postinstall (node install.cjs) timed out");
                return false;
            }

            if (p.ExitCode != 0)
            {
                var stderr = await SafeReadAsync(stderrTask, logger);
                await SafeReadAsync(stdoutTask, logger);
                logger.LogWarning("postinstall (node install.cjs) exited {ExitCode}: {Stderr}", p.ExitCode, Truncate(stderr));
                return false;
            }

            await SafeReadAsync(stdoutTask, logger);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "postinstall (node install.cjs) failed to start");
            return false;
        }
    }
}
