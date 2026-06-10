using System.Text.Json;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Pty;

/// <summary>
/// Discovers the live Copilot CLI model catalog by driving the interactive
/// <c>/model</c> picker in a real pseudo-terminal. Result is cached on disk
/// so we don't pay the (~6s) PTY round-trip on every page load.
///
/// Why PTY: the CLI exposes no machine-readable list-models command. The picker
/// is the single source of truth — it mirrors what GitHub currently offers
/// for the authenticated user, including multipliers and which model is the
/// account default vs. the user's selection.
/// </summary>
public sealed class CopilotModelDiscovery
{
    private static readonly Regex ModelLineRegex = new(
        @"^\s*[\u276F>\?\*]?\s*(?<label>[A-Za-z][A-Za-z0-9 .\-_]*?)(?:\s*\(default\))?\s*(?<sel>\u2713|\u2714|\u2705)?\s+(?<mult>\d+(?:\.\d+)?)x\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly ILogger<CopilotModelDiscovery> _logger;
    private readonly CopilotCliEnvironment _env;
    private readonly IConfiguration _config;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CliModelCatalog? _memCache;
    private DateTime _memCacheAt = DateTime.MinValue;

    public CopilotModelDiscovery(
        ILogger<CopilotModelDiscovery> logger,
        CopilotCliEnvironment env,
        IConfiguration config)
    {
        _logger = logger;
        _env = env;
        _config = config;
    }

    private string CachePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "agent-taskboard");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "model-catalog.json");
        }
    }

    private TimeSpan Ttl =>
        TimeSpan.FromMinutes(_config.GetValue<int?>("CopilotModelsCacheMinutes") ?? 60);

    public async Task<CliModelCatalog> GetAsync(string cliPath, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh)
        {
            if (_memCache != null && DateTime.UtcNow - _memCacheAt < Ttl) return _memCache;
            var fromDisk = TryLoadDisk();
            if (fromDisk != null && DateTime.UtcNow - fromDisk.FetchedAt < Ttl)
            {
                _memCache = fromDisk;
                _memCacheAt = fromDisk.FetchedAt;
                // Refresh the active-marker against settings.json (which can change
                // independently of the picker) so the dropdown stays accurate.
                return WithActiveModelApplied(fromDisk);
            }
        }

        await _gate.WaitAsync(ct);
        try
        {
            // Re-check after gate
            if (!forceRefresh && _memCache != null && DateTime.UtcNow - _memCacheAt < Ttl)
                return _memCache;

            try
            {
                var fresh = await DiscoverViaPtyAsync(cliPath, ct);
                _memCache = fresh;
                _memCacheAt = fresh.FetchedAt;
                TrySaveDisk(fresh);
                return fresh;
            }
            catch (Exception ex)
            {
                // PTY discovery is inherently racy (depends on terminal render
                // timing). Don't 500 the API: fall back to whatever we have on
                // disk, even if it's stale. The "Source" field flags it so
                // callers can tell the difference; a manual refresh re-tries.
                _logger.LogWarning(ex, "PTY model discovery failed; falling back to cached catalog");
                if (_memCache != null) return WithSource(_memCache, "pty-failed-mem-cache");
                var fromDisk = TryLoadDisk();
                if (fromDisk != null)
                {
                    _memCache = fromDisk;
                    _memCacheAt = fromDisk.FetchedAt;
                    return WithSource(WithActiveModelApplied(fromDisk), "pty-failed-disk-cache");
                }
                // No cache at all → propagate so the endpoint can return a
                // proper error rather than an empty catalog the UI would
                // silently render as "no models available".
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private static CliModelCatalog WithSource(CliModelCatalog cat, string source)
        => cat with { Source = source };

    private CliModelCatalog WithActiveModelApplied(CliModelCatalog cat)
    {
        var active = _env.ReadActiveModel();
        if (string.IsNullOrWhiteSpace(active)) return cat;
        var models = cat.Models.Select(m => m with
        {
            IsDefault = string.Equals(m.Id, active, StringComparison.OrdinalIgnoreCase)
        }).ToList();
        return cat with { Models = models };
    }

    private async Task<CliModelCatalog> DiscoverViaPtyAsync(string cliPath, CancellationToken ct)
    {
        // Use a scratch folder so we don't accidentally trust a real workspace
        // just to enumerate models.
        var scratch = Path.Combine(Path.GetTempPath(), "agent-taskboard-pty-scratch");
        Directory.CreateDirectory(scratch);
        _env.EnsureFolderTrusted(scratch);
        _env.EnsureTerminalSetupAcknowledged("vscode", "vscode-insiders", "windows-terminal");

        _logger.LogInformation("Spawning Copilot CLI in PTY for /model discovery");
        await using var pty = await PtySession.SpawnAsync(
            app: cliPath,
            cwd: scratch,
            ct: ct);

        // Wait for the prompt to be ready (settle once).
        await pty.WaitForIdleAsync(idleMs: 1500, timeoutMs: 8000, ct);

        await pty.SendKeysAsync("/model<Enter>", ct);

        var headerRegex = new Regex(@"Select Model", RegexOptions.IgnoreCase);
        var headerMatch = await pty.WaitForPatternAsync(headerRegex, timeoutMs: 6000, ct);
        if (headerMatch == null)
        {
            _logger.LogWarning("Copilot /model picker did not appear in PTY");
            await pty.SendKeysAsync("<Esc>", ct);
            throw new InvalidOperationException("Model picker did not appear");
        }

        // Let the picker fully render.
        await pty.WaitForIdleAsync(idleMs: 700, timeoutMs: 3000, ct);

        var snapshot = pty.SnapshotStripped();

        // Esc out cleanly.
        try { await pty.SendKeysAsync("<Esc>", ct); } catch (Exception __ex) { SilentCatch.Note(__ex, "CopilotModelDiscovery:160"); }

        var models = ParsePickerSnapshot(snapshot);
        if (models.Count == 0)
        {
            _logger.LogWarning("PTY model discovery captured 0 models. Snapshot tail:\n{Tail}",
                snapshot.Length > 800 ? snapshot[^800..] : snapshot);
            throw new InvalidOperationException("No models parsed from picker");
        }

        return WithActiveModelApplied(new CliModelCatalog
        {
            Models = models,
            Source = "cli-pty",
            FetchedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Parse the ANSI-stripped snapshot. We look for lines of the shape
    /// <c>"Claude Sonnet 4.6        1x"</c> (optionally with a selection
    /// cursor, a "(default)" suffix, or a check mark). The "Auto" entry is
    /// excluded — it's not a concrete model.
    /// </summary>
    public static List<CliModelInfo> ParsePickerSnapshot(string snapshot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CliModelInfo>();
        foreach (Match m in ModelLineRegex.Matches(snapshot))
        {
            var rawLabel = m.Groups["label"].Value.Trim();
            if (string.IsNullOrEmpty(rawLabel)) continue;
            if (string.Equals(rawLabel, "Auto", StringComparison.OrdinalIgnoreCase)) continue;
            if (rawLabel.Length > 60) continue; // sanity guard — picker labels are short
            if (!double.TryParse(m.Groups["mult"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mult))
                continue;

            var id = LabelToId(rawLabel);
            if (!seen.Add(id)) continue;

            result.Add(new CliModelInfo
            {
                Id = id,
                Label = rawLabel,
                Vendor = GuessVendor(id),
                Multiplier = mult,
                IsDefault = false // overridden later from settings.json
            });
        }
        return result;
    }

    private static string LabelToId(string label) =>
        Regex.Replace(label.Trim().ToLowerInvariant(), @"\s+", "-");

    private static string? GuessVendor(string id)
    {
        if (id.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) return "anthropic";
        if (id.StartsWith("gpt",    StringComparison.OrdinalIgnoreCase)) return "openai";
        if (id.StartsWith("o1",     StringComparison.OrdinalIgnoreCase)) return "openai";
        if (id.StartsWith("o3",     StringComparison.OrdinalIgnoreCase)) return "openai";
        if (id.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)) return "google";
        if (id.StartsWith("grok",   StringComparison.OrdinalIgnoreCase)) return "xai";
        return null;
    }

    private CliModelCatalog? TryLoadDisk()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var json = File.ReadAllText(CachePath);
            return JsonSerializer.Deserialize<CliModelCatalog>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load model catalog cache");
            return null;
        }
    }

    private void TrySaveDisk(CliModelCatalog cat)
    {
        try { File.WriteAllText(CachePath, JsonSerializer.Serialize(cat, JsonOpts)); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to persist model catalog cache"); }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
