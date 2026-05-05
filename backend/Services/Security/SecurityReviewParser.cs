using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrchestratorApi.Services.Security;

/// <summary>
/// Pure parser for the structured block in a security review Markdown file.
/// Two shapes are accepted (slice 1 of the quality-system mockup,
/// docs/mockups/quality-system/taxonomy.md "Report Contracts"):
///
/// <list type="bullet">
///   <item>YAML-style frontmatter at the very top of the file, fenced by
///   <c>---</c> on its own line. Keys are flat <c>key: value</c> pairs;
///   the parser also recognises one indented level for severity splits
///   (e.g. <c>severities:\n  critical: 0\n</c>).</item>
///   <item>A fenced JSON block anywhere in the file (preferentially the
///   last one), opened with <c>```json</c>. JSON wins if both are present
///   so a producer can stamp typed evidence at the bottom even when the
///   frontmatter is human-curated.</item>
/// </list>
///
/// Graceful degradation is part of the contract: when neither shape is
/// present <see cref="SecurityReviewParseResult.ParseOk"/> is false, the
/// caller renders the raw Markdown with the "unstructured report"
/// warning, and no fields are inferred from prose.
/// </summary>
public static class SecurityReviewParser
{
    private static readonly Regex FrontmatterRegex = new(
        @"\A---\s*\r?\n(?<body>[\s\S]*?)\r?\n---\s*\r?\n",
        RegexOptions.Compiled);

    private static readonly Regex JsonFenceRegex = new(
        @"```\s*json\s*\r?\n(?<body>[\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions JsonParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Tries to parse a structured block from the file body. JSON fenced
    /// block takes precedence over YAML frontmatter when both are present.
    /// </summary>
    public static SecurityReviewParseResult Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new SecurityReviewParseResult(
                ParseOk: false,
                ParseError: "empty file",
                Fields: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
        }

        // Prefer the LAST JSON fence: producers append evidence at the end,
        // and a fence inside an example block earlier in the doc should not
        // shadow the canonical sidecar. Falls through to frontmatter when no
        // JSON fence is present.
        var lastFence = LastJsonFence(markdown);
        if (lastFence is not null)
        {
            try
            {
                var fields = ParseJsonObjectFlat(lastFence);
                return new SecurityReviewParseResult(
                    ParseOk: true,
                    ParseError: null,
                    Fields: fields);
            }
            catch (JsonException ex)
            {
                return new SecurityReviewParseResult(
                    ParseOk: false,
                    ParseError: $"JSON sidecar failed to parse: {ex.Message}",
                    Fields: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
            }
        }

        var fmMatch = FrontmatterRegex.Match(markdown);
        if (fmMatch.Success)
        {
            try
            {
                var fields = ParseFlatYaml(fmMatch.Groups["body"].Value);
                if (fields.Count == 0)
                {
                    return new SecurityReviewParseResult(
                        ParseOk: false,
                        ParseError: "frontmatter present but yielded no recognised keys",
                        Fields: fields);
                }
                return new SecurityReviewParseResult(
                    ParseOk: true,
                    ParseError: null,
                    Fields: fields);
            }
            catch (Exception ex)
            {
                return new SecurityReviewParseResult(
                    ParseOk: false,
                    ParseError: $"frontmatter failed to parse: {ex.Message}",
                    Fields: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
            }
        }

        return new SecurityReviewParseResult(
            ParseOk: false,
            ParseError: "no structured frontmatter or JSON sidecar found",
            Fields: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
    }

    private static string? LastJsonFence(string markdown)
    {
        Match? last = null;
        foreach (Match m in JsonFenceRegex.Matches(markdown))
            last = m;
        return last?.Groups["body"].Value;
    }

    /// <summary>
    /// Walks a JSON object into a flat dictionary the consumer can pull
    /// known keys from. Nested objects are flattened as nested dictionaries
    /// so the consumer can reach <c>severities.high</c> without re-parsing.
    /// Arrays land as <see cref="object"/>[] and primitives as their CLR type.
    /// </summary>
    private static Dictionary<string, object?> ParseJsonObjectFlat(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException($"top-level JSON must be an object, was {doc.RootElement.ValueKind}.");
        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
            fields[prop.Name] = JsonElementToValue(prop.Value);
        return fields;
    }

    private static object? JsonElementToValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(
            p => p.Name,
            p => JsonElementToValue(p.Value),
            StringComparer.OrdinalIgnoreCase),
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToValue).ToArray(),
        _ => null,
    };

    /// <summary>
    /// Minimal YAML parser. Supports flat <c>key: value</c> pairs at the
    /// root and one indented level (two-space indent) under a parent key
    /// that has no inline value. Quotes and surrounding whitespace are
    /// trimmed; numeric values become <see cref="long"/>, booleans become
    /// <see cref="bool"/>, anything else stays a string. Comments after
    /// <c>#</c> are stripped.
    /// </summary>
    private static Dictionary<string, object?> ParseFlatYaml(string body)
    {
        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, object?>? nested = null;
        string? nestedKey = null;
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) { nested = null; nestedKey = null; continue; }
            // Strip a trailing "# comment" but only when the # is unquoted.
            var commentStart = FindUnquotedHash(line);
            if (commentStart >= 0) line = line[..commentStart].TrimEnd();
            if (line.Length == 0) continue;

            if (line.StartsWith("  ") && nested is not null && nestedKey is not null)
            {
                var indented = line[2..];
                var (k2, v2, hasValue2) = SplitKeyValue(indented);
                if (k2 is null || !hasValue2) continue;
                nested[k2] = CoerceScalar(v2);
                continue;
            }

            // Top-level entry resets the nested-map cursor.
            nested = null;
            nestedKey = null;

            var (k, v, hasValue) = SplitKeyValue(line);
            if (k is null) continue;
            if (!hasValue || string.IsNullOrEmpty(v))
            {
                // Parent for an indented child block follows on next lines.
                var child = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                fields[k] = child;
                nested = child;
                nestedKey = k;
                continue;
            }
            fields[k] = CoerceScalar(v);
        }
        return fields;
    }

    private static (string? Key, string Value, bool HasValue) SplitKeyValue(string line)
    {
        var idx = line.IndexOf(':');
        if (idx < 0) return (null, string.Empty, false);
        var key = line[..idx].Trim();
        if (key.Length == 0) return (null, string.Empty, false);
        var rest = line[(idx + 1)..];
        var value = rest.Trim();
        // A trailing colon with nothing else means "child block follows".
        if (value.Length == 0) return (key, string.Empty, false);
        // Strip surrounding quotes for plain string values.
        if ((value.StartsWith('"') && value.EndsWith('"')) ||
            (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            value = value.Length >= 2 ? value[1..^1] : value;
        }
        return (key, value, true);
    }

    private static int FindUnquotedHash(string line)
    {
        char? quote = null;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is null)
            {
                if (c == '"' || c == '\'') quote = c;
                else if (c == '#') return i;
            }
            else if (c == quote)
            {
                quote = null;
            }
        }
        return -1;
    }

    private static object? CoerceScalar(string raw)
    {
        if (raw.Length == 0) return raw;
        if (long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i))
            return i;
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase)) return null;
        return raw;
    }

    /// <summary>
    /// Pulls a known scalar key out of a parsed field map. Used by the
    /// service layer to lift the small number of fields the cards need
    /// without exposing the loose <c>object?</c> shape to callers.
    /// </summary>
    public static string? GetString(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
            bool b => b ? "true" : "false",
            _ => v.ToString(),
        };
    }

    public static int? GetInt(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            long l => (int?)l,
            int i => i,
            double d => (int?)d,
            string s when int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
            _ => null,
        };
    }

    /// <summary>
    /// Reads a nested map of string -> int (e.g. <c>severities</c>).
    /// Returns null when the key is missing or the shape doesn't match.
    /// </summary>
    public static IReadOnlyDictionary<string, int>? GetIntMap(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var v) || v is null) return null;
        if (v is not IDictionary<string, object?> map) return null;
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in map)
        {
            var n = kv.Value switch
            {
                long l => (int?)l,
                int i => i,
                double d => (int?)d,
                string s when int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
                _ => (int?)null,
            };
            if (n is not null) result[kv.Key] = n.Value;
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>Same as <see cref="GetIntMap"/> but for nested string maps (e.g. <c>severityThresholds</c>).</summary>
    public static IReadOnlyDictionary<string, string>? GetStringMap(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var v) || v is null) return null;
        if (v is not IDictionary<string, object?> map) return null;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in map)
        {
            var s = kv.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(s)) result[kv.Key] = s.Trim();
        }
        return result.Count == 0 ? null : result;
    }
}
