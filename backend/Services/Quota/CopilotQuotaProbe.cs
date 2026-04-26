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
/// </summary>
public sealed class CopilotQuotaProbe : QuotaProbeBase
{
    private static readonly Regex RemainingPctRegex = new(
        @"Remaining\s+reqs\.?\s*[:\-]?\s*([\-+]?\d+(?:\.\d+)?)\s*%",
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

            return new QuotaSnapshot
            {
                CliType = CliType,
                Plan = plan,
                Source = "/usage",
                RawSample = TruncateForDebug(snap, 600),
                Windows =
                [
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
                ]
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
