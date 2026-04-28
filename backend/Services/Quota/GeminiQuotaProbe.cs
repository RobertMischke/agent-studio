using System.Diagnostics;
using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Pty;

namespace OrchestratorApi.Services.Quota;

/// <summary>
/// Probes Google's <c>gemini</c> CLI for plan / identity. Quota windows are
/// not yet surfaced because the CLI's daily quota is fetched dynamically by
/// <c>refreshAvailableCredits()</c> against an authenticated Google endpoint
/// and only rendered inside the interactive <c>/stats model</c> panel — there
/// is no headless mode for it. We therefore:
/// <list type="bullet">
///   <item>Read <c>~/.gemini/google_accounts.json</c> for the active user.</item>
///   <item>Read <c>~/.gemini/settings.json</c> for the auth type (oauth-personal vs api-key).</item>
///   <item>(Best-effort) capture the default model from the CLI's <c>init</c> stream-json frame.</item>
/// </list>
/// The result has an empty <c>Windows[]</c>; the side-sheet renders the identity
/// instead. Reset / pooled-limit numbers can be added later via interactive
/// PTY scraping if the user demand justifies the complexity.
/// </summary>
public sealed class GeminiQuotaProbe : QuotaProbeBase
{
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
                return new QuotaSnapshot { CliType = CliType, Source = "headless", Error = "gemini CLI not available" };

            var (email, authType) = ReadIdentity();
            var defaultModel = await ProbeDefaultModelAsync(resolvedPath, ct);

            var plan = NormalizePlan(authType, _configuration["Quota:GeminiPlan"]);
            var label = email is { Length: > 0 } e ? $"Gemini ({e})" : "Gemini";

            return new QuotaSnapshot
            {
                CliType   = CliType,
                Plan      = plan,
                Source    = "headless",
                RawSample = $"email={email}; authType={authType}; defaultModel={defaultModel}",
                Windows   =
                [
                    new QuotaWindow
                    {
                        Label      = label,
                        UsedPct    = null,
                        Unit       = null,
                        ResetAt    = null,
                        ResetLabel = defaultModel is { Length: > 0 } dm ? $"Default model: {dm}" : null
                    }
                ],
                // Surface the limitation honestly so users don't expect numbers.
                Error = "Gemini quota numbers (daily limit, reset time) require an interactive panel scrape — not yet implemented. See docs/supported-clis.md §3.4."
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini quota probe failed");
            return new QuotaSnapshot { CliType = CliType, Source = "headless", Error = ex.Message };
        }
    }

    private static (string? Email, string? AuthType) ReadIdentity()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir  = Path.Combine(home, ".gemini");

        string? email = null;
        var accounts = Path.Combine(dir, "google_accounts.json");
        if (File.Exists(accounts))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(accounts));
                if (doc.RootElement.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.String)
                    email = a.GetString();
            }
            catch { /* best-effort */ }
        }

        string? authType = null;
        var settings = Path.Combine(dir, "settings.json");
        if (File.Exists(settings))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settings));
                if (doc.RootElement.TryGetProperty("security", out var sec)
                    && sec.TryGetProperty("auth", out var au)
                    && au.TryGetProperty("selectedType", out var st)
                    && st.ValueKind == JsonValueKind.String)
                    authType = st.GetString();
            }
            catch { /* best-effort */ }
        }

        return (email, authType);
    }

    /// <summary>
    /// Fires a 1-token headless prompt and parses the <c>init</c> frame for the
    /// default model. Wrapped in a tight timeout so a stuck CLI never blocks the probe.
    /// </summary>
    private async Task<string?> ProbeDefaultModelAsync(string cliPath, CancellationToken ct)
    {
        try
        {
            var scratch = Path.Combine(Path.GetTempPath(), "agent-taskboard-quota", CliType);
            Directory.CreateDirectory(scratch);

            var psi = new ProcessStartInfo
            {
                FileName  = cliPath,
                Arguments = "-p \"ok\" -o stream-json --skip-trust -y",
                WorkingDirectory       = scratch,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding  = System.Text.Encoding.UTF8
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(cts.Token)) != null)
            {
                line = line.TrimStart();
                if (!line.StartsWith('{') || !line.Contains("\"type\":\"init\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        return m.GetString();
                    }
                }
                catch { /* swallow malformed line */ }
            }

            try { proc.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gemini headless model probe failed");
            return null;
        }
    }

    private static string? NormalizePlan(string? authType, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return authType switch
        {
            "oauth-personal"   => "Personal (OAuth)",
            "gemini-api-key"   => "API Key",
            "vertex-ai"        => "Vertex AI",
            null               => null,
            _                  => authType
        };
    }
}
