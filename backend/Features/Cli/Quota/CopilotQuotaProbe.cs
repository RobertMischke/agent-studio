using System.Globalization;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Pty;

namespace OrchestratorApi.Services.Quota;

/// <summary>
/// Probes the GitHub Copilot CLI home-screen footer for the
/// "Remaining reqs.: ±NN.N%" line that appears once the welcome banner finishes
/// loading. Combines the percentage with the user-configured plan (Pro=300/mo,
/// Pro+=1500/mo, Business=300/mo) to derive absolute used/limit numbers. The
/// reset date is the first of the next month at 00:00 UTC (GitHub Copilot
/// premium-request counter rolls over monthly).
///
/// The probe spawns the bundled native <c>copilot.exe</c> directly rather than
/// the <c>copilot</c> npm shim: under ConPTY the shim's cmd.exe → node wrapper
/// chain swallows the TUI render, leaving an empty snapshot. See
/// <see cref="ResolveCopilotNativeExe"/>.
///
/// Copilot MAX plans additionally surface a Sonnet-model budget line. The
/// parser checks for it intelligently and adds a second QuotaWindow when present
/// so the UI can render an extra bar.
/// </summary>
public sealed class CopilotQuotaProbe : QuotaProbeBase
{
    private static readonly Regex RemainingPctRegex = new(
        @"Remaining\s+reqs\.?\s*[:\-]?\s*([\-+]?\d+(?:\.\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Matches a Sonnet Maximum/Max usage line in any of its likely formats:
    ///   "Sonnet Maximum Usage: 45%"
    ///   "Sonnet Maximum: remaining 45.0%"
    ///   "Claude Sonnet Max: 45%"
    ///   "Maximum Sonnet: remaining 45%"
    /// Group 1 captures the optional "remaining" qualifier; group 2 captures the number.
    /// When group 1 is present the number is remaining%, so usedPct = 100 - number.
    /// When absent the number is already usedPct.
    /// </summary>
    private static readonly Regex SonnetMaximumPctRegex = new(
        @"(?:(?:claude\s+)?sonnet\s+max(?:imum)?(?:\s+usage)?|max(?:imum)?\s+(?:claude\s+)?sonnet)" +
        @"\s*[:\-]?\s*(remaining\s+)?([\-+]?\d+(?:\.\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IConfiguration _configuration;

    public CopilotQuotaProbe(
        ILogger<CopilotQuotaProbe> logger,
        CliRouter router,
        CopilotCliEnvironment env,
        IConfiguration configuration)
        : base(logger, router, env)
    {
        _configuration = configuration;
    }

    public override string CliType => CliTypes.Copilot;

    private const string SourceLabel = "home-screen footer";

    public override async Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var cli = _router.Get(CliType);
            var (available, _, resolvedPath) = cli.TestCliPath();
            if (!available)
                return new QuotaSnapshot { CliType = CliType, Source = SourceLabel, Error = "copilot CLI not available" };

            // The `copilot` entry on PATH is an npm shim: under ConPTY it runs
            // cmd.exe → node (npm-loader spawnSync, stdio:inherit) → the native
            // copilot.exe, and that wrapper chain swallows the TUI render so the
            // PTY snapshot comes back empty (the historical "rawSample empty /
            // could not find Remaining reqs" failure). Spawning the bundled
            // native exe directly makes the home screen actually paint.
            var nativeExe = ResolveCopilotNativeExe(CliExecutionServiceBase.ResolveExecutable(resolvedPath));
            _logger.LogDebug("Copilot quota probe spawning {Exe} (resolved from {Resolved})", nativeExe, resolvedPath);

            var scratch = Path.Combine(Path.GetTempPath(), "agent-taskboard-quota", CliType);
            Directory.CreateDirectory(scratch);
            try
            {
                _env.EnsureFolderTrusted(scratch);
                _env.EnsureTerminalSetupAcknowledged("vscode", "vscode-insiders", "windows-terminal");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pre-probe environment setup failed (best-effort)");
            }

            await using var pty = await PtySession.SpawnAsync(app: nativeExe, cwd: scratch, ct: ct);

            // The "Remaining reqs.: NN.N%" figure lives in the home-screen footer
            // — no /usage keystroke needed. The native exe paints several seconds
            // after spawn, far longer than an idle-detect window would wait, so we
            // block on the figure itself (with the banner as a liveness fallback)
            // instead of on output settling.
            var footerPattern = new Regex(@"Remaining\s+reqs|Copilot\s+v\d", RegexOptions.IgnoreCase);
            var matched = await pty.WaitForPatternAsync(footerPattern, timeoutMs: 25000, ct);
            await pty.WaitForIdleAsync(idleMs: 700, timeoutMs: 4000, ct);

            var snap = pty.SnapshotStripped();
            try { await pty.SendKeysAsync("<Esc><Esc>", ct); } catch { }

            if (matched == null)
                _logger.LogInformation("Copilot quota probe: footer pattern not seen within timeout; parsing best-effort snapshot");

            var plan = (_configuration["Quota:CopilotPlan"] ?? "Pro").Trim();
            return ParseSnapshot(snap, plan, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot quota probe failed");
            return new QuotaSnapshot
            {
                CliType = CliType,
                Source = SourceLabel,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Parses the ANSI-stripped home-screen snapshot into a quota snapshot.
    /// Visible for testing so the captured real-world footer can be replayed
    /// through the parser without a live PTY.
    ///
    /// <para>
    /// When the "Remaining reqs.: NN.N%" figure is present we emit a populated
    /// premium-requests window (plus a Sonnet Maximum window on MAX plans).
    /// When it is absent — banner-only render, signed-out, or a future CLI that
    /// drops the footer line — we return a clean <em>unavailable</em> snapshot
    /// (no windows, no <see cref="QuotaSnapshot.Error"/>) so the status bar
    /// shows "—" rather than a red error pill. Copilot's headless surface has
    /// no documented command that prints the remaining-requests counter; the
    /// interactive footer is the only source, so its absence is a known
    /// limitation, not a probe failure.
    /// </para>
    /// </summary>
    public static QuotaSnapshot ParseSnapshot(string? snap, string plan, DateTime now)
    {
        var resetAt = NextMonthStartUtc(now);
        var match = RemainingPctRegex.Match(snap ?? "");
        if (!match.Success)
        {
            return new QuotaSnapshot
            {
                CliType = CliTypes.Copilot,
                Plan = plan,
                Source = SourceLabel,
                RawSample = TruncateForDebug(snap),
                Windows = new List<QuotaWindow>()
            };
        }

        var remainingPct = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var usedPct = Math.Round(100.0 - remainingPct, 1);

        var limit = PlanToLimit(plan);
        double? used = limit.HasValue ? Math.Round(limit.Value * usedPct / 100.0, 0) : null;

        var windows = new List<QuotaWindow>
        {
            new QuotaWindow
            {
                Label = "Premium requests (monthly)",
                UsedPct = usedPct,
                Used = used,
                Limit = limit,
                Unit = "requests",
                ResetAt = resetAt,
                ResetLabel = resetAt.ToString("MMM 1", CultureInfo.InvariantCulture)
            }
        };

        var sonnetWindow = TryParseSonnetMaximum(snap!, resetAt);
        if (sonnetWindow != null)
            windows.Add(sonnetWindow);

        return new QuotaSnapshot
        {
            CliType = CliTypes.Copilot,
            Plan = plan,
            Source = SourceLabel,
            RawSample = TruncateForDebug(snap, 600),
            Windows = windows
        };
    }

    /// <summary>
    /// Resolves the npm <c>copilot</c> shim (a <c>.cmd</c>/<c>.ps1</c> launcher,
    /// or the extension-less Bash shim) to the bundled native
    /// <c>copilot.exe</c> so the PTY can drive it directly instead of through
    /// the cmd.exe → node wrapper chain (which swallows the TUI render under
    /// ConPTY). Mirrors <c>ClaudeCliService.ResolveCmdShimToExe</c>. Falls back
    /// to the input path when the native exe can't be located (portable install,
    /// non-Windows, or a layout change) — the probe then degrades to the
    /// unavailable state rather than throwing.
    /// </summary>
    internal static string ResolveCopilotNativeExe(string shimOrExePath)
    {
        if (string.IsNullOrWhiteSpace(shimOrExePath)) return shimOrExePath;
        if (!OperatingSystem.IsWindows()) return shimOrExePath;
        // Already the native exe.
        if (shimOrExePath.EndsWith("copilot.exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(shimOrExePath))
            return shimOrExePath;

        var dir = Path.GetDirectoryName(shimOrExePath);
        if (string.IsNullOrEmpty(dir)) return shimOrExePath;

        // npm global-bin layout: <bin>/copilot.cmd alongside
        // <bin>/node_modules/@github/copilot/node_modules/@github/copilot-win32-x64/copilot.exe
        var candidate = Path.Combine(
            dir, "node_modules", "@github", "copilot",
            "node_modules", "@github", "copilot-win32-x64", "copilot.exe");
        return File.Exists(candidate) ? candidate : shimOrExePath;
    }

    /// <summary>
    /// Attempts to parse a Sonnet Maximum budget line from the /usage snapshot.
    /// Returns a populated QuotaWindow when the line is found, null otherwise.
    /// Public so unit tests can call it directly without a live PTY.
    /// </summary>
    public static QuotaWindow? TryParseSonnetMaximum(string snap, DateTime resetAt)
    {
        var m = SonnetMaximumPctRegex.Match(snap);
        if (!m.Success) return null;

        var isRemaining = m.Groups[1].Length > 0;
        var pct = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var usedPct = isRemaining ? Math.Round(100.0 - pct, 1) : Math.Round(pct, 1);

        return new QuotaWindow
        {
            Label = "Sonnet Maximum (monthly)",
            UsedPct = usedPct,
            Unit = "%",
            ResetAt = resetAt,
            ResetLabel = resetAt.ToString("MMM 1", CultureInfo.InvariantCulture)
        };
    }

    private static double? PlanToLimit(string plan) => plan.ToLowerInvariant() switch
    {
        "pro+" or "pro plus" or "proplus" => 1500,
        "business" => 300,
        "enterprise" => 1000,
        "free" => 50,
        "pro" => 300,
        _ => null
    };

    private static DateTime NextMonthStartUtc(DateTime now)
    {
        var firstOfThisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return firstOfThisMonth.AddMonths(1);
    }
}
