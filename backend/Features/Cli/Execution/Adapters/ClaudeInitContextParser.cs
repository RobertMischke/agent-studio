using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// The fields Claude Code reports in its <c>--output-format stream-json</c>
/// init frame (<c>{"type":"system","subtype":"init",...}</c>) beyond the
/// session id the run loop already captures. The CLI tells us exactly what it
/// loaded for the run - the model, the effective permission mode, the working
/// directory, the wired-in MCP servers, the output style, and where the API
/// key came from. All of it is discarded today; this record carries it so the
/// read-only execution-context surface (ASS-1739 / T1a) can show what the agent
/// actually saw without re-deriving it from convention.
/// </summary>
public sealed record ClaudeInitContext
{
    public string? SessionId { get; init; }
    public string? Model { get; init; }
    public string? Cwd { get; init; }

    /// <summary>The CLI's own term: <c>bypassPermissions</c> / <c>acceptEdits</c> / <c>plan</c> / <c>default</c>.</summary>
    public string? PermissionMode { get; init; }

    /// <summary>e.g. <c>none</c>, <c>ANTHROPIC_API_KEY</c>, <c>/login managed key</c>.</summary>
    public string? ApiKeySource { get; init; }

    /// <summary>The active output style, when reported (<c>default</c> etc.).</summary>
    public string? OutputStyle { get; init; }

    /// <summary>MCP servers the CLI wired into the run, with their reported status.</summary>
    public List<ClaudeMcpServer> McpServers { get; init; } = [];

    /// <summary>Count of tools the init frame advertised (the names are not retained).</summary>
    public int ToolCount { get; init; }

    /// <summary>Count of slash commands the init frame advertised.</summary>
    public int SlashCommandCount { get; init; }
}

/// <summary>One MCP server entry from the Claude init frame.</summary>
public sealed record ClaudeMcpServer
{
    public string Name { get; init; } = "";
    public string? Status { get; init; }
}

/// <summary>
/// Pure parser for Claude's stream-json init frame. Side-effect free and
/// dependency free so it is directly unit-testable; the Claude driver calls it
/// from its output hook the moment the frame arrives. Mirrors the defensive
/// "try every property, tolerate a missing one" shape of
/// <see cref="ClaudeEventAdapter"/> so a future CLI schema tweak degrades to a
/// partially-filled context rather than throwing.
/// </summary>
public static class ClaudeInitContextParser
{
    /// <summary>
    /// Parse one stdout line. Returns true and fills <paramref name="context"/>
    /// only when the line is the JSON object with
    /// <c>type=system</c> and <c>subtype=init</c>; every other line (non-JSON,
    /// other frame types, empty) returns false and leaves the out param null.
    /// </summary>
    public static bool TryParse(string? jsonLine, out ClaudeInitContext? context)
    {
        context = null;
        if (string.IsNullOrWhiteSpace(jsonLine) || jsonLine.TrimStart()[0] != '{') return false;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(jsonLine); }
        catch { return false; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
            if (!string.Equals(type, "system", StringComparison.Ordinal)
                || !string.Equals(subtype, "init", StringComparison.Ordinal))
                return false;

            context = new ClaudeInitContext
            {
                SessionId = Str(root, "session_id"),
                Model = Str(root, "model"),
                Cwd = Str(root, "cwd"),
                PermissionMode = Str(root, "permissionMode"),
                ApiKeySource = Str(root, "apiKeySource"),
                OutputStyle = Str(root, "output_style"),
                McpServers = ReadMcpServers(root),
                ToolCount = CountArray(root, "tools"),
                SlashCommandCount = CountArray(root, "slash_commands"),
            };
            return true;
        }
    }

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString())
            : null;

    private static int CountArray(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.GetArrayLength()
            : 0;

    private static List<ClaudeMcpServer> ReadMcpServers(JsonElement root)
    {
        var list = new List<ClaudeMcpServer>();
        if (!root.TryGetProperty("mcp_servers", out var servers) || servers.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var s in servers.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            var name = s.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var status = s.TryGetProperty("status", out var st) ? st.GetString() : null;
            list.Add(new ClaudeMcpServer { Name = name!, Status = status });
        }
        return list;
    }
}
