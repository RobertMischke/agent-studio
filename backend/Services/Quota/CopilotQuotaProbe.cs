using System.Globalization;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Pty;

namespace OrchestratorApi.Services.Quota;

/// <summary>
/// Probes the GitHub Copilot CLI footer for the "Remaining reqs.: ±NN.N%" line
/// that appears after the welcome banner finishes loading. Combines the
/// percentage with the user-configured plan (Pro=300/mo, Pro+=1500/mo,
/// Business=300/mo) to derive absolute used/limit numbers. The reset date is
/// the first of the next month at 00:00 UTC (GitHub Copilot premium-request
/// counter rolls over monthly).
///
/// Copilot MAX plans additionally show a Sonnet-model budget in the /usage
/// output. The parser checks for that line intelligently and adds a second
/// QuotaWindow when present so the UI can render an extra bar.
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

    public override async Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
    {
        try
        {
            // The footer with "Remaining reqs.: ..." appears after the banner is rendered;
            // sending /usage forces the latest counter to be fetched server-side.
            var snap = await ProbeWithSnapshotAsync(
                sendKeys: "/usage<Enter>",
                initialIdleMs: 8000,
                settleAfterSendMs: 6000,
                ct);

            var match = RemainingPctRegex.Match(snap);
            if (!match.Success)
            {
                return new QuotaSnapshot
                {
                    CliType = CliType,
                    Source = "/usage",
                    RawSample = TruncateForDebug(snap),
                    Error = "Could not find 'Remaining reqs.: NN%' line in CLI output."
                };
            }

            var remainingPct = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var usedPct = Math.Round(100.0 - remainingPct, 1);

            var plan = (_configuration["Quota:CopilotPlan"] ?? "Pro").Trim();
            var limit = PlanToLimit(plan);
            double? used = limit.HasValue ? Math.Round(limit.Value * usedPct / 100.0, 0) : null;

            var resetAt = NextMonthStartUtc(DateTime.UtcNow);

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

            var sonnetWindow = TryParseSonnetMaximum(snap, resetAt);
            if (sonnetWindow != null)
                windows.Add(sonnetWindow);

            return new QuotaSnapshot
            {
                CliType = CliType,
                Plan = plan,
                Source = "/usage",
                RawSample = TruncateForDebug(snap, 600),
                Windows = windows
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot quota probe failed");
            return new QuotaSnapshot
            {
                CliType = CliType,
                Source = "/usage",
                Error = ex.Message
            };
        }
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
