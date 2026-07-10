using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.Cli;

/// <summary>
/// Probes OpenAI's <c>codex</c> CLI for plan + 5h/weekly quota via the
/// <c>/status</c> slash command, which renders a panel like:
/// <code>
/// Account:        robertmischke@gmail.com (Plus)
/// 5h limit:       [░░░░░░░░░░] 0% left (resets 02:33)
/// Weekly limit:   [██████████] 84% left (resets 21:33 on 3 May)
/// </code>
/// IMPORTANT: Codex reports <b>% left</b> (the bar visually shows the remaining
/// budget). We invert (<c>usedPct = 100 - left</c>) so the model is consistent
/// with the other probes and the UI can colour-code high values as critical.
///
/// In the PTY snapshot inter-word spaces are usually collapsed
/// (<c>5hlimit:[░░░░]0%left(resets02:33)</c>), so the regexes are intentionally permissive.
/// </summary>
public sealed class CodexQuotaProbe : QuotaProbeBase
{
    // "5h limit: [bar] NN% left (resets HH:MM[ on D Mon])"
    private static readonly Regex FiveHourRegex = new(
        @"5h\s*limit\s*:?\s*\[[^\]]*\]\s*(?<left>\d+)\s*%\s*left[^()]*\(\s*resets\s*(?<reset>[^)]+?)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WeeklyRegex = new(
        @"Weekly\s*limit\s*:?\s*\[[^\]]*\]\s*(?<left>\d+)\s*%\s*left[^()]*\(\s*resets\s*(?<reset>[^)]+?)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Header of the Spark sub-block: "<model>-Spark limit:". It is
    // DELIBERATELY version-agnostic. The previous form pinned the model to
    // "GPT-5.3-Codex-Spark"; when the CLI bumped the Spark model (5.3 -> 5.6),
    // the header stopped matching, the standard/spark split silently collapsed,
    // and the standard-window regexes then latched onto the Spark block's
    // near-empty "% left" line - reporting an exhausted 5-hour/Weekly window as
    // ~1-4% used. That is the AGT-2064 false-snapshot glitch. Only the Spark
    // sub-panel header contains the word "Spark", so matching on "Spark limit"
    // alone is both sufficient and immune to future model renames.
    private static readonly Regex SparkHeaderRegex = new(
        @"Spark\s*limit\s*:?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Account: someone@example.com (Plus)" — captures the parenthesised plan name.
    private static readonly Regex PlanRegex = new(
        @"Account\s*:?[^()\r\n]{1,120}\(\s*(?<plan>[A-Za-z][A-Za-z0-9 +]{0,30})\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Fallback footer when /status couldn't render: "5h NN% · weekly NN%".
    // The footer values are also "% left", so we invert.
    private static readonly Regex FooterRegex = new(
        @"5h\s*(?<h5left>\d+)\s*%\s*[·•]\s*weekly\s*(?<wkleft>\d+)\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public CodexQuotaProbe(
        ILogger<CodexQuotaProbe> logger,
        CliRouter router,
        CliEnvironment env)
        : base(logger, router, env) { }

    public override string CliType => CliTypes.Codex;

    public override async Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var trustPattern   = new Regex(@"trust\s*the\s*contents|Yes,\s*continue", RegexOptions.IgnoreCase);
            var welcomePattern = new Regex(@"OpenAI\s*Codex|model:", RegexOptions.IgnoreCase);
            var statusPattern  = new Regex(@"5h\s*limit|Weekly\s*limit|Account:", RegexOptions.IgnoreCase);

            var snap = await ProbeWithStepsAsync(
            [
                // Codex's trust prompt has "1. Yes, continue" pre-selected and accepts a
                // bare Enter. Sending "1<Enter>" works for confirmation but ALSO leaves a
                // stray "1" in the chat input box, which then prefixes the next slash
                // command and turns "/status" into a chat message instead of a command.
                new ProbeStep("await-trust",   WaitForPattern: trustPattern,   WaitTimeoutMs: 10000, SendKeys: "<Enter>", SettleTimeoutMs: 6000, PreSendDelayMs: 300),
                // Clear the buffer so the await-welcome match only sees post-trust content.
                new ProbeStep("await-welcome", ClearBufferBefore: true, WaitForPattern: welcomePattern, WaitTimeoutMs: 10000, SendKeys: "/status", SettleIdleMs: 800, SettleTimeoutMs: 3000, PreSendDelayMs: 800),
                // Send Enter as a separate keystroke — Codex sometimes drops a fast-following
                // Enter while it's still parsing the slash command.
                new ProbeStep("submit-status", PreSendDelayMs: 500, SendKeys: "<Enter>", SettleIdleMs: 1500, SettleTimeoutMs: 8000),
                new ProbeStep("await-status",  WaitForPattern: statusPattern,  WaitTimeoutMs: 10000, SettleIdleMs: 1500, SettleTimeoutMs: 6000)
            ],
            initialIdleMs: 8000,
            ct);

            string? plan = PlanRegex.Match(snap) is { Success: true } pm
                ? pm.Groups["plan"].Value.Trim()
                : null;

            var windows = ParseStatusWindows(snap);

            // Log the parsed windows next to the raw sample so a future false
            // snapshot (AGT-2064) is diagnosable from logs alone, without having
            // to reconstruct the PTY transcript. Kept at Information because a
            // quota probe is a low-frequency, high-value observability surface.
            var sparkSeen = SparkHeaderRegex.IsMatch(snap);
            _logger.LogInformation(
                "codex_quota_probe_parsed plan={Plan} sparkBlock={Spark} windows=[{Windows}]",
                plan ?? "<none>",
                sparkSeen,
                string.Join(", ", windows.Select(w => $"{w.Label}={w.UsedPct}%")));

            return new QuotaSnapshot
            {
                CliType   = CliType,
                Plan      = plan,
                Source    = "/status",
                RawSample = TruncateForDebug(snap),
                Windows   = windows,
                Error     = (plan == null && windows.Count == 0)
                    ? "Could not parse Codex /status output."
                    : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Codex quota probe failed");
            return new QuotaSnapshot { CliType = CliType, Source = "/status", Error = ex.Message };
        }
    }

    public static List<QuotaWindow> ParseStatusWindows(string snap)
    {
        var windows = new List<QuotaWindow>();
        var sparkHeader = SparkHeaderRegex.Match(snap);
        // The standard 5-hour/Weekly windows are read ONLY from the region above
        // the Spark header. This is what keeps the Spark sub-block's own
        // near-empty 5h/Weekly lines from being mistaken for the main windows
        // (AGT-2064). See SparkHeaderRegex for why the header match is
        // version-agnostic.
        var standardSnap = sparkHeader.Success ? snap[..sparkHeader.Index] : snap;

        AddLimitWindow(windows, standardSnap, FiveHourRegex, "5-hour");
        AddLimitWindow(windows, standardSnap, WeeklyRegex, "Weekly");

        if (sparkHeader.Success)
        {
            var sparkSnap = snap[(sparkHeader.Index + sparkHeader.Length)..];
            AddLimitWindow(windows, sparkSnap, FiveHourRegex, "Spark 5-hour");
            AddLimitWindow(windows, sparkSnap, WeeklyRegex, "Spark Weekly");
        }

        // Footer fallback when /status didn't render: at least we still
        // get usage percentages, just no reset time.
        if (windows.Count == 0 && FooterRegex.Match(snap) is { Success: true } fmm
            && int.TryParse(fmm.Groups["h5left"].Value, out var h5)
            && int.TryParse(fmm.Groups["wkleft"].Value, out var wk))
        {
            windows.Add(new QuotaWindow { Label = "5-hour", UsedPct = 100 - h5, Unit = "%" });
            windows.Add(new QuotaWindow { Label = "Weekly", UsedPct = 100 - wk, Unit = "%" });
        }

        return windows;
    }

    private static void AddLimitWindow(List<QuotaWindow> windows, string snap, Regex regex, string label)
    {
        if (regex.Match(snap) is not { Success: true } match
            || !int.TryParse(match.Groups["left"].Value, out var left))
        {
            return;
        }

        var resetRaw = match.Groups["reset"].Value.Trim();
        windows.Add(new QuotaWindow
        {
            Label      = label,
            UsedPct    = 100 - left,
            Unit       = "%",
            ResetAt    = ParseResetUtc(resetRaw),
            ResetLabel = resetRaw
        });
    }

    /// <summary>
    /// Codex reset strings come in two shapes:
    ///  - "02:33"               (the next 24h occurrence in the user's local TZ)
    ///  - "21:33 on 3 May"      (a specific upcoming weekly date)
    /// Returns the UTC instant or null if the format doesn't match.
    /// </summary>
    private static DateTime? ParseResetUtc(string raw)
    {
        var local = TimeZoneInfo.Local;
        var nowLocal = DateTime.Now;

        // "HH:MM on D Mon" — weekly variant.
        var withDate = Regex.Match(raw, @"^(?<time>\d{1,2}:\d{2})\s*on\s*(?<day>\d{1,2})\s*(?<mon>[A-Za-z]+)$", RegexOptions.IgnoreCase);
        if (withDate.Success)
        {
            if (DateTime.TryParseExact(
                    withDate.Groups["time"].Value, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                && int.TryParse(withDate.Groups["day"].Value, out var day)
                && TryParseMonth(withDate.Groups["mon"].Value, out var month))
            {
                var year = nowLocal.Year;
                var candidate = new DateTime(year, month, day, t.Hour, t.Minute, 0, DateTimeKind.Unspecified);
                if (candidate < nowLocal.AddDays(-1)) candidate = candidate.AddYears(1);
                return TimeZoneInfo.ConvertTimeToUtc(candidate, local);
            }
            return null;
        }

        // Plain "HH:MM" — next future occurrence today/tomorrow.
        if (DateTime.TryParseExact(raw, new[] { "H:mm", "HH:mm", "h:mmtt", "hh:mmtt" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var hm))
        {
            var candidate = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, hm.Hour, hm.Minute, 0, DateTimeKind.Unspecified);
            if (candidate <= nowLocal) candidate = candidate.AddDays(1);
            return TimeZoneInfo.ConvertTimeToUtc(candidate, local);
        }

        return null;
    }

    private static bool TryParseMonth(string s, out int month)
    {
        var formats = new[] { "MMM", "MMMM" };
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            month = dt.Month;
            return true;
        }
        month = 0;
        return false;
    }
}
