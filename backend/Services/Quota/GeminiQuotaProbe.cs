using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Pty;

namespace OrchestratorApi.Services.Quota;

/// <summary>
/// Probes Google's <c>gemini</c> CLI for plan, identity, and the daily quota
/// window via the interactive <c>/stats model</c> panel.
///
/// The CLI's daily quota is fetched dynamically by <c>refreshAvailableCredits()</c>
/// against an authenticated Google endpoint and only rendered inside the
/// <c>/stats model</c> panel. There is no headless command for it, so we drive
/// the interactive UI through a PTY:
/// <list type="number">
///   <item>Pre-trust the scratch folder in <c>~/.gemini/trustedFolders.json</c>
///         (simpler than answering the in-CLI trust prompt).</item>
///   <item>Spawn <c>gemini -m auto-gemini-3 --skip-trust</c>. Forcing the auto
///         model is what makes the panel render the QuotaStatsInfo block.</item>
///   <item>Dismiss the IDE-connect / Shift+Enter setup modals if they appear.</item>
///   <item>Send a one-char prompt so the session has at least one active-model
///         entry — without it, <c>/stats model</c> short-circuits with
///         "No API calls have been made in this session." That single prompt
///         is the cost of this probe; the cache TTL keeps it bounded.</item>
///   <item>Send <c>/stats model</c> and parse the panel for "X% used",
///         "Usage limit: N", and "Limit resets in &lt;duration&gt;".</item>
/// </list>
/// On free / non-paid tiers the QuotaStatsInfo block doesn't render
/// (refreshAvailableCredits is a no-op there); we then surface the identity
/// fields without an error and an explanatory note.
/// </summary>
public sealed class GeminiQuotaProbe : QuotaProbeBase
{
    // "0% used (Limit resets in 24h)" / "98% used (Limit resets in 30m)" / "Limit reached, resets in 5h 12m"
    private static readonly Regex UsedPctRegex = new(
        @"(?<pct>\d+)\s*%\s*used(?:\s*\(\s*Limit\s*resets\s*in\s*(?<reset>[^)]+?)\s*\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LimitReachedRegex = new(
        @"Limit\s*reached(?:\s*,\s*resets\s*in\s*(?<reset>[^\r\n│]+?)(?:\s*$|[\s│]))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Usage limit: 200" — number may include thousands separators (gemini uses
    // locale-aware toLocaleString, so "1,000" or "1.000" are both possible).
    private static readonly Regex UsageLimitRegex = new(
        @"Usage\s*limit\s*:\s*(?<limit>[\d][\d.,\s]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Tier:                       Gemini Code Assist in Google One AI Pro"
    private static readonly Regex TierRegex = new(
        @"Tier\s*:\s*(?<tier>.+?)(?:\s*│|\s{2,}│|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // "Auth Method:                Signed in with Google (someone@example.com)"
    private static readonly Regex AuthEmailRegex = new(
        @"Signed\s*in\s*with\s*Google\s*\(\s*(?<email>[^\s)]+@[^\s)]+)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Reset duration ("24h", "5h 12m", "30m"). Returned by gemini's
    // formatResetTime(time, "terse"). We compute UTC by adding to "now".
    private static readonly Regex ResetDurationRegex = new(
        @"^\s*(?:(?<h>\d+)\s*h)?\s*(?:(?<m>\d+)\s*m)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IConfiguration _configuration;

    public GeminiQuotaProbe(
        ILogger<GeminiQuotaProbe> logger,
        CliRouter router,
        CopilotCliEnvironment env,
        IConfiguration configuration)
        : base(logger, router, env)
    {
        _configuration = configuration;
    }

    public override string CliType => CliTypes.Gemini;

    public override async Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var cli = _router.Get(CliType);
            var (available, _, resolvedPath) = cli.TestCliPath();
            if (!available)
                return new QuotaSnapshot { CliType = CliType, Source = "/stats model", Error = "gemini CLI not available" };

            var scratch = Path.Combine(Path.GetTempPath(), "agent-taskboard-quota", CliType);
            Directory.CreateDirectory(scratch);
            try { EnsureGeminiFolderTrusted(scratch); }
            catch (Exception ex) { _logger.LogDebug(ex, "Pre-trust of Gemini scratch folder failed (best-effort)"); }

            // Force the auto-gemini-3 tier so /stats model renders QuotaStatsInfo
            // (it only shows when isAutoModel(currentModel) is true).
            await using var pty = await PtySession.SpawnAsync(
                app: resolvedPath,
                args: ["-m", "auto-gemini-3", "--skip-trust"],
                cwd: scratch,
                cols: 200,
                rows: 60,
                ct: ct);

            // Wait for the welcome banner that includes the plan label so we know the UI is live.
            var welcome = new Regex(@"Gemini\s*CLI\s*v|Plan\s*:|Type\s*your\s*message", RegexOptions.IgnoreCase);
            await pty.WaitForPatternAsync(welcome, timeoutMs: 12000, ct);
            await pty.WaitForIdleAsync(idleMs: 1200, timeoutMs: 6000, ct);

            // Dismiss the most common one-time prompts. They appear in random order.
            for (int i = 0; i < 4; i++)
            {
                var s = pty.SnapshotStripped();
                if (s.Contains("Shift+Enter") || s.Contains("multiline input"))
                {
                    await pty.SendKeysAsync("2<Enter>", ct);
                    await pty.WaitForIdleAsync(idleMs: 800, timeoutMs: 4000, ct);
                    continue;
                }
                if (s.Contains("connect IDE") || s.Contains("Gemini CLI Companion"))
                {
                    await pty.SendKeysAsync("3<Enter>", ct);
                    await pty.WaitForIdleAsync(idleMs: 800, timeoutMs: 4000, ct);
                    continue;
                }
                break;
            }

            // Send a one-char prompt to populate active-model metrics — without
            // this the /stats model panel shows "No API calls have been made"
            // and skips QuotaStatsInfo entirely.
            pty.ClearBuffer();
            await pty.SendKeysAsync("ok", ct);
            await Task.Delay(500, ct);
            await pty.SendKeysAsync("<Enter>", ct);
            // The prompt response includes the QuotaStatsInfo data via response.remainingCredits.
            // Wait for the response to settle, then send /stats model.
            var afterResponse = new Regex(@"Type\s*your\s*message|press\s*tab\s*twice", RegexOptions.IgnoreCase);
            await pty.WaitForPatternAsync(afterResponse, timeoutMs: 25000, ct);
            await pty.WaitForIdleAsync(idleMs: 1500, timeoutMs: 8000, ct);

            // Send /stats model and wait for the panel.
            pty.ClearBuffer();
            await pty.SendKeysAsync("/stats model", ct);
            await Task.Delay(400, ct);
            await pty.SendKeysAsync("<Enter>", ct);
            var statsPanel = new Regex(@"Stats\s*For\s*Nerds|No\s*API\s*calls\s*have\s*been\s*made", RegexOptions.IgnoreCase);
            await pty.WaitForPatternAsync(statsPanel, timeoutMs: 8000, ct);
            await pty.WaitForIdleAsync(idleMs: 1000, timeoutMs: 4000, ct);

            var snap = pty.SnapshotStripped();
            try { await pty.SendKeysAsync("<Esc><Esc>", ct); } catch { }

            return ParseSnapshot(snap);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini quota probe failed");
            return new QuotaSnapshot { CliType = CliType, Source = "/stats model", Error = ex.Message };
        }
    }

    /// <summary>
    /// Parses the ANSI-stripped <c>/stats model</c> snapshot. Visible for testing.
    /// </summary>
    public static QuotaSnapshot ParseSnapshot(string snap)
    {
        string? plan = TierRegex.Match(snap) is { Success: true } tm
            ? Trim(tm.Groups["tier"].Value)
            : null;
        string? email = AuthEmailRegex.Match(snap) is { Success: true } am
            ? am.Groups["email"].Value.Trim()
            : null;

        double? usedPct = null;
        string? resetLabel = null;
        DateTime? resetAt = null;
        double? limit = null;

        if (UsedPctRegex.Match(snap) is { Success: true } um
            && double.TryParse(um.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
        {
            usedPct = p;
            if (um.Groups["reset"].Success)
            {
                resetLabel = Trim(um.Groups["reset"].Value);
                resetAt = ParseResetDuration(resetLabel);
            }
        }
        else if (LimitReachedRegex.Match(snap) is { Success: true } lm)
        {
            usedPct = 100;
            if (lm.Groups["reset"].Success)
            {
                resetLabel = Trim(lm.Groups["reset"].Value);
                resetAt = ParseResetDuration(resetLabel);
            }
        }

        if (UsageLimitRegex.Match(snap) is { Success: true } lim)
        {
            // "1,000" / "1.000" / "1 000" — strip separators, parse invariant.
            var raw = new string(lim.Groups["limit"].Value.Where(char.IsDigit).ToArray());
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var lv))
                limit = lv;
        }

        var label = email is { Length: > 0 }
            ? $"Daily ({email})"
            : "Daily";

        var windows = new List<QuotaWindow>();
        // Only emit a quota window when we actually have a number; otherwise the
        // identity-only path (free tier, missing /stats model panel) returns no
        // windows so the UI doesn't show a misleading "?" donut.
        if (usedPct.HasValue || limit.HasValue)
        {
            windows.Add(new QuotaWindow
            {
                Label      = label,
                UsedPct    = usedPct,
                Used       = (usedPct.HasValue && limit.HasValue)
                    ? Math.Round(limit.Value * usedPct.Value / 100.0, 0)
                    : null,
                Limit      = limit,
                Unit       = limit.HasValue ? "requests" : "%",
                ResetAt    = resetAt,
                ResetLabel = resetLabel
            });
        }

        string? error = null;
        if (windows.Count == 0)
        {
            // Identity captured but the panel didn't render quota numbers.
            // Free-tier users land here. Surface the cause without a hard error.
            error = email is { Length: > 0 }
                ? "Plan does not expose a daily quota panel (free tier or unauthenticated probe)."
                : "Could not capture Gemini quota panel — see source for details.";
        }

        return new QuotaSnapshot
        {
            CliType   = CliTypes.Gemini,
            Plan      = plan,
            Source    = "/stats model",
            RawSample = TruncateForDebug(snap),
            Windows   = windows,
            Error     = error
        };
    }

    /// <summary>
    /// Adds the scratch folder to <c>~/.gemini/trustedFolders.json</c> so the CLI
    /// renders the welcome banner without the trust modal blocking us first.
    /// Schema: <c>{ "&lt;path&gt;": "TRUST_FOLDER" }</c>. Idempotent.
    /// </summary>
    private static void EnsureGeminiFolderTrusted(string folder)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir  = Path.Combine(home, ".gemini");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "trustedFolders.json");

        // Gemini uses forward slashes + lower-case drive letter for keys.
        var key = folder.Replace('\\', '/');
        if (key.Length >= 2 && key[1] == ':') key = char.ToLowerInvariant(key[0]) + key[1..];

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                            dict[prop.Name] = prop.Value.GetString()!;
                    }
                }
            }
            catch { /* corrupted file — overwrite below */ }
        }

        if (dict.TryGetValue(key, out var existing) && existing == "TRUST_FOLDER") return;
        dict[key] = "TRUST_FOLDER";
        File.WriteAllText(path, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Parses gemini's terse reset format ("24h", "5h 12m", "30m") and returns
    /// the next reset instant in UTC.
    /// </summary>
    private static DateTime? ParseResetDuration(string raw)
    {
        var m = ResetDurationRegex.Match(raw);
        if (!m.Success) return null;
        var hours   = m.Groups["h"].Success && int.TryParse(m.Groups["h"].Value, out var h) ? h : 0;
        var minutes = m.Groups["m"].Success && int.TryParse(m.Groups["m"].Value, out var mn) ? mn : 0;
        if (hours == 0 && minutes == 0) return null;
        return DateTime.UtcNow.AddHours(hours).AddMinutes(minutes);
    }

    private static string Trim(string s) => s.Replace('│', ' ').Trim();
}
