using System.Globalization;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Pty;

namespace OrchestratorApi.Services.Quota;

/// <summary>
/// Probes Anthropic's <c>claude</c> CLI for plan + quota info via the <c>/usage</c>
/// slash command, which renders a panel like:
/// <code>
/// Current session            ██████████████████ 104% used
///                            Resets 3:40am (Europe/Berlin)
/// Current week (all models)  ██████████████      28% used
///                            Resets Apr 28, 2pm (Europe/Berlin)
/// </code>
/// In the PTY snapshot inter-word spaces are usually collapsed
/// (e.g. <c>Currentsession...104%usedResets3:40am(Europe/Berlin)</c>),
/// so all parsing regexes are intentionally permissive about whitespace.
///
/// Notes:
///  - <c>/usage</c> works even when over-quota; we no longer fall back to the
///    welcome banner alone.
///  - Plan is read from the welcome banner (<c>Claude Pro</c> / <c>Claude Max</c>) and,
///    when missing, from the configured <c>Quota:ClaudePlan</c> setting.
///  - The "Current session" bucket can read above 100% (extra usage / overages);
///    we surface the raw value so the UI can colour-code it.
/// </summary>
public sealed class ClaudeQuotaProbe : QuotaProbeBase
{
    private static readonly Regex PlanRegex = new(
        @"Claude\s*(Pro\+?|Pro\s*Plus|Free|Team|Max(?:\s*\d+x)?|Enterprise)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Current session ... NN% used Resets H:MMam (Tz)"
    private static readonly Regex SessionRegex = new(
        @"Current\s*session[^\d]{0,160}?(?<pct>\d+)\s*%\s*used[^R]{0,40}?Resets\s*(?<time>\d{1,2}(?::\d{2})?\s*(?:am|pm)?)\s*(?:\((?<tz>[^)]+)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Current week (all models) ... NN% used Resets Mon DD, H[:MM][am|pm] (Tz)"
    // or the compact Claude variant: "Currentweek(allmodels)...32%usedResets2pm(Europe/Berlin)"
    private static readonly Regex WeekRegex = new(
        @"Current\s*week\s*\(\s*all\s*models?\s*\)[^\d]{0,220}?(?<pct>\d+)\s*%\s*used[^R]{0,60}?Resets\s*(?<reset>(?:[A-Za-z]+\s*\d+\s*,?\s*)?\d{1,2}(?::\d{2})?\s*(?:am|pm)?)\s*(?:\((?<tz>[^)]+)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Inline rate-limit warning Claude prints when over quota — used as a
    // last-ditch fallback for the session reset time when /usage failed.
    private static readonly Regex RateLimitResetRegex = new(
        @"out\s*of\s*(?<bucket>\w+)\s*usage[^·]*·\s*resets\s*(?<time>\d{1,2}:\d{2}\s*(?:am|pm)?)\s*(?:\((?<tz>[^\)]+)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Banner is present (we got a session) but the /usage panel never rendered."
    // When this matches and Windows ends up empty, log Warn so a future CLI-output
    // format change is visible in logs instead of silently returning windows: [].
    private static readonly Regex WelcomeBannerRegex = new(
        @"Claude\s*Code\s*v|Welcome\s*back|Tips\s*for\s*getting\s*started",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IConfiguration _configuration;

    public ClaudeQuotaProbe(
        ILogger<ClaudeQuotaProbe> logger,
        CliRouter router,
        CopilotCliEnvironment env,
        IConfiguration configuration)
        : base(logger, router, env)
    {
        _configuration = configuration;
    }

    public override string CliType => CliTypes.Claude;

    public override async Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var trustPattern   = new Regex(@"trust\s*this\s*folder|Quick\s*safety\s*check", RegexOptions.IgnoreCase);
            var welcomePattern = new Regex(@"Claude\s*Code\s*v|Welcome\s*back|Tips\s*for\s*getting\s*started", RegexOptions.IgnoreCase);
            var usagePattern   = new Regex(@"Current\s*session|Current\s*week", RegexOptions.IgnoreCase);

            // Two-step: confirm trust, wait for welcome, send /usage, wait for the
            // usage panel to render. /usage works even when over quota.
            //
            // The trust step uses SendKeysOnlyIfMatched: once trust has been accepted
            // for the scratch folder (Claude persists "hasTrustDialogAccepted: true" in
            // ~/.claude.json keyed by absolute path), the dialog never appears again
            // and blindly sending "1<Enter>" leaks "1" into the chat input box as a
            // real prompt — which then turns the following "/usage" into a chat reply
            // instead of a slash command, leaving windows[] empty.
            var snap = await ProbeWithStepsAsync(
            [
                new ProbeStep("await-trust",   WaitForPattern: trustPattern,   WaitTimeoutMs: 4000, SendKeys: "1<Enter>", SettleTimeoutMs: 6000, SendKeysOnlyIfMatched: true),
                new ProbeStep("await-welcome", WaitForPattern: welcomePattern, WaitTimeoutMs: 10000, SendKeys: "/usage<Enter>", SettleIdleMs: 1500, SettleTimeoutMs: 8000),
                new ProbeStep("await-usage",   WaitForPattern: usagePattern,   WaitTimeoutMs: 8000,  SettleIdleMs: 1200, SettleTimeoutMs: 5000)
            ],
            initialIdleMs: 8000,
            ct);

            string? plan = PlanRegex.Match(snap) is { Success: true } pm
                ? NormalizePlan(pm.Groups[1].Value)
                : null;
            if (plan == null)
            {
                var configured = _configuration["Quota:ClaudePlan"];
                if (!string.IsNullOrWhiteSpace(configured)) plan = NormalizePlan(configured);
            }

            var windows = ParseUsageWindows(snap);

            // Fallback: if /usage didn't render but the rate-limit warning did,
            // synthesize at least the session bucket so the user has *something*.
            if (windows.Count == 0 && RateLimitResetRegex.Match(snap) is { Success: true } rm)
            {
                var time = rm.Groups["time"].Value.Trim();
                var tz   = rm.Groups["tz"].Success ? rm.Groups["tz"].Value.Trim() : null;
                windows.Add(new QuotaWindow
                {
                    Label      = $"Out of {rm.Groups["bucket"].Value} usage",
                    UsedPct    = 100,
                    Unit       = "%",
                    ResetAt    = ParseResetTimeUtc(time, tz),
                    ResetLabel = tz != null ? $"{time} ({tz})" : time
                });
            }

            if (LooksLikeParserDrift(snap, windows))
            {
                _logger.LogWarning(
                    "Claude /usage probe returned 0 windows but the welcome banner is present in the snapshot. " +
                    "Likely causes: (1) /usage output format changed in a new Claude Code release and the parser regexes need updating, " +
                    "or (2) the slash command never executed (chat-input leak from a stale trust step). " +
                    "Raw sample (tail): {Sample}",
                    TruncateForDebug(snap, 400));
            }

            return new QuotaSnapshot
            {
                CliType   = CliType,
                Plan      = plan,
                Source    = "/usage",
                RawSample = TruncateForDebug(snap),
                Windows   = windows,
                Error     = (plan == null && windows.Count == 0)
                    ? "Could not parse plan or quota info from Claude /usage panel."
                    : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claude quota probe failed");
            return new QuotaSnapshot { CliType = CliType, Source = "/usage", Error = ex.Message };
        }
    }

    /// <summary>
    /// Drift detector: true when the snapshot clearly contains a Claude session
    /// (banner / welcome-back / tips line) but the parser produced no windows.
    /// That combination is the signature of a CLI-output format change or a
    /// probe-step bug that prevented /usage from rendering. Public so tests can
    /// pin the heuristic against the cached real-world rawSample.
    /// </summary>
    public static bool LooksLikeParserDrift(string snap, IReadOnlyCollection<QuotaWindow> windows)
    {
        if (windows.Count > 0) return false;
        if (string.IsNullOrEmpty(snap)) return false;
        return WelcomeBannerRegex.IsMatch(snap);
    }

    private static string NormalizePlan(string raw)
    {
        var t = raw.Trim();
        if (t.Equals("Pro Plus", StringComparison.OrdinalIgnoreCase) || t.Equals("ProPlus", StringComparison.OrdinalIgnoreCase))
            return "Pro+";
        return t;
    }

    public static List<QuotaWindow> ParseUsageWindows(string snap)
    {
        var windows = new List<QuotaWindow>();

        if (SessionRegex.Match(snap) is { Success: true } sm
            && double.TryParse(sm.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sPct))
        {
            var time = sm.Groups["time"].Value.Trim();
            var tz   = sm.Groups["tz"].Success ? sm.Groups["tz"].Value.Trim() : null;
            windows.Add(new QuotaWindow
            {
                Label      = "Current session (5h)",
                UsedPct    = Math.Min(100, sPct),
                Unit       = "%",
                ResetAt    = ParseResetTimeUtc(time, tz),
                ResetLabel = tz != null ? $"{time} ({tz})" : time
            });
        }

        if (WeekRegex.Match(snap) is { Success: true } wm
            && double.TryParse(wm.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var wPct))
        {
            var reset = wm.Groups["reset"].Value.Trim();
            var tz    = wm.Groups["tz"].Success ? wm.Groups["tz"].Value.Trim() : null;
            var hasDate = Regex.IsMatch(reset, @"[A-Za-z]+\s*\d+", RegexOptions.IgnoreCase);
            windows.Add(new QuotaWindow
            {
                Label      = "Weekly (all models)",
                UsedPct    = Math.Min(100, wPct),
                Unit       = "%",
                ResetAt    = hasDate ? ParseResetDateUtc(reset, tz) : ParseResetTimeUtc(reset, tz),
                ResetLabel = tz != null ? $"{reset} ({tz})" : reset
            });
        }

        return windows;
    }

    /// <summary>
    /// Parses "3:40am" / "15:30" with an optional IANA timezone in parens and
    /// returns the next future occurrence of that wall-clock time in UTC.
    /// </summary>
    private static DateTime? ParseResetTimeUtc(string timeStr, string? ianaTz)
    {
        var formats = new[] { "h:mmtt", "hh:mmtt", "htt", "hhtt", "H:mm", "HH:mm" };
        var clean = timeStr.Replace(" ", "");
        if (!DateTime.TryParseExact(clean, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;

        TimeZoneInfo tz;
        try { tz = ianaTz != null ? TimeZoneInfo.FindSystemTimeZoneById(ianaTz) : TimeZoneInfo.Local; }
        catch { tz = TimeZoneInfo.Local; }

        var nowInTz   = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);
        var resetInTz = new DateTime(nowInTz.Year, nowInTz.Month, nowInTz.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);
        if (resetInTz <= nowInTz) resetInTz = resetInTz.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(resetInTz, tz);
    }

    /// <summary>
    /// Parses "Apr 28, 2pm" or "Apr 28, 14:30" with an optional IANA timezone
    /// and returns the corresponding UTC instant.
    /// </summary>
    private static DateTime? ParseResetDateUtc(string dateStr, string? ianaTz)
    {
        // Normalise "Apr 28, 2pm" → "Apr 28 2pm" so a single set of formats covers both.
        var clean = dateStr.Replace(",", " ").Replace("  ", " ").Trim();
        var formats = new[]
        {
            "MMM d htt", "MMM d hh tt", "MMM d h:mmtt", "MMM d hh:mmtt",
            "MMM d HH:mm", "MMM d H:mm",
            "MMMM d htt", "MMMM d h:mmtt", "MMMM d HH:mm"
        };
        if (!DateTime.TryParseExact(clean, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
            return null;

        TimeZoneInfo tz;
        try { tz = ianaTz != null ? TimeZoneInfo.FindSystemTimeZoneById(ianaTz) : TimeZoneInfo.Local; }
        catch { tz = TimeZoneInfo.Local; }

        // Year is missing from the input — assume the current year, roll to next year if past.
        var nowInTz = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);
        var year = nowInTz.Year;
        var candidate = new DateTime(year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);
        if (candidate < nowInTz.AddDays(-1)) candidate = candidate.AddYears(1);
        return TimeZoneInfo.ConvertTimeToUtc(candidate, tz);
    }
}
