using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrchestratorApi.Services.Pty;

/// <summary>
/// Manages persistent state in <c>~/.copilot/{config,settings}.json</c> so that
/// PTY-spawned sessions skip the trust dialog and the terminal-setup dialog.
///
/// Also exposes the user's current default model directly from
/// <c>settings.json</c> — the only authoritative source for it.
/// </summary>
public sealed class CopilotCliEnvironment
{
    private readonly ILogger<CopilotCliEnvironment> _logger;
    private readonly object _writeLock = new();

    public CopilotCliEnvironment(ILogger<CopilotCliEnvironment> logger)
    {
        _logger = logger;
    }

    public string CopilotHome =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");

    public string ConfigPath   => Path.Combine(CopilotHome, "config.json");
    public string SettingsPath => Path.Combine(CopilotHome, "settings.json");

    /// <summary>
    /// Returns the model the user has currently selected in the CLI, or null
    /// if <c>settings.json</c> doesn't exist or has no <c>model</c> field.
    /// </summary>
    public string? ReadActiveModel()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var node = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
            return node?["model"]?.GetValue<string>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read Copilot settings.json");
            return null;
        }
    }

    /// <summary>
    /// Ensure <paramref name="folder"/> appears in <c>config.json:trustedFolders</c>.
    /// Idempotent. Creates the file if missing.
    /// </summary>
    public bool EnsureFolderTrusted(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        var normalized = NormalizePath(folder);
        return MutateConfig(obj =>
        {
            var arr = obj["trustedFolders"] as JsonArray ?? new JsonArray();
            foreach (var entry in arr)
            {
                var s = entry?.GetValue<string>();
                if (s != null && string.Equals(NormalizePath(s), normalized, StringComparison.OrdinalIgnoreCase))
                    return false; // already present
            }
            arr.Add(folder);
            obj["trustedFolders"] = arr;
            return true;
        });
    }

    /// <summary>
    /// Ensure <paramref name="terminalId"/> is in <c>config.json:askedSetupTerminals</c>
    /// so the multi-line key-binding dialog never fires for headless PTY sessions.
    /// Recommended values: <c>"vscode"</c>, <c>"vscode-insiders"</c>, <c>"windows-terminal"</c>.
    /// </summary>
    public bool EnsureTerminalSetupAcknowledged(params string[] terminalIds)
    {
        return MutateConfig(obj =>
        {
            var arr = obj["askedSetupTerminals"] as JsonArray ?? new JsonArray();
            var existing = arr.Select(x => x?.GetValue<string>()).Where(s => s != null)
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var id in terminalIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (existing.Add(id))
                {
                    arr.Add(id);
                    changed = true;
                }
            }
            if (changed) obj["askedSetupTerminals"] = arr;
            return changed;
        });
    }

    private bool MutateConfig(Func<JsonObject, bool> mutate)
    {
        lock (_writeLock)
        {
            try
            {
                Directory.CreateDirectory(CopilotHome);
                JsonObject obj;
                if (File.Exists(ConfigPath))
                {
                    var text = File.ReadAllText(ConfigPath);
                    // Strip the leading "// User settings…" line comment Copilot writes.
                    var jsonStart = text.IndexOf('{');
                    if (jsonStart < 0) obj = new JsonObject();
                    else obj = JsonNode.Parse(text[jsonStart..]) as JsonObject ?? new JsonObject();
                }
                else
                {
                    obj = new JsonObject();
                }

                if (!mutate(obj)) return false;

                var serialized = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                var tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, "// User settings belong in settings.json.\n// This file is managed automatically.\n" + serialized);
                File.Move(tmp, ConfigPath, overwrite: true);
                _logger.LogInformation("Updated Copilot config at {Path}", ConfigPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mutate Copilot config.json");
                return false;
            }
        }
    }

    private static string NormalizePath(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
        catch { return p; }
    }
}
