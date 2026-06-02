using System.Text.Json;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Result of parsing an epic decomposition (planning) run's agent output.
/// <see cref="SubTasks"/> is the list the runner feeds into the same
/// sub-task creation path as <c>POST /api/epics/{id}/sub-tasks</c>;
/// <see cref="Error"/> is a human-readable reason when nothing usable was
/// found (an empty list with a non-null error is the "agent produced no
/// actionable plan" outcome).
/// </summary>
public sealed record EpicDecompositionResult(IReadOnlyList<EpicSubTaskSpec> SubTasks, string? Error)
{
    public bool HasSubTasks => SubTasks.Count > 0;
}

/// <summary>
/// Pure parser for the planning/decomposition run (ADR-0032 contract-bounded
/// agent: the agent authors a structured plan, this deterministic reader turns
/// it into specs). The agent is asked to end its run with a single fenced JSON
/// block of the shape:
/// <code>
/// {
///   "subTasks": [
///     { "title": "...", "prompt": "..." }
///   ]
/// }
/// </code>
/// Parsing is deliberately tolerant: it accepts the last fenced block, a bare
/// fenced array, or raw JSON embedded in prose; it reads either <c>prompt</c>
/// or <c>promptMarkdown</c>, either <c>cli</c> or <c>cliType</c>; and it skips
/// entries with a blank title rather than failing the whole plan, so a
/// partially-good decomposition still lands its valid sub-tasks. No I/O, no DI:
/// the side-effecting half (creating the sub-tasks) lives in the runner.
/// </summary>
public static class EpicDecompositionParser
{
    private static readonly Regex FenceRegex = new(
        @"```(?:json)?\s*\r?\n(?<body>[\s\S]*?)\r?\n?```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Parse the joined CLI output lines into sub-task specs. Never throws -
    /// a malformed or empty output returns an empty list with an explanatory
    /// <see cref="EpicDecompositionResult.Error"/>.
    /// </summary>
    public static EpicDecompositionResult Parse(IReadOnlyList<string>? outputLines)
        => Parse(outputLines is null ? string.Empty : string.Join("\n", outputLines));

    /// <summary>Parse the raw agent output text into sub-task specs.</summary>
    public static EpicDecompositionResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new EpicDecompositionResult([], "planning run produced no output");

        // Prefer fenced code blocks, last one first: the agent is told to end
        // with the plan, and a later block overrides an earlier scratch block.
        var fenced = FenceRegex.Matches(text);
        for (var i = fenced.Count - 1; i >= 0; i--)
        {
            if (TryExtractSpecs(fenced[i].Groups["body"].Value, out var specs))
                return Finalize(specs);
        }

        // Fallback: raw JSON embedded in prose with no fence. Try the widest
        // object, then the widest array.
        foreach (var candidate in GreedyJsonCandidates(text))
        {
            if (TryExtractSpecs(candidate, out var specs))
                return Finalize(specs);
        }

        return new EpicDecompositionResult([], "no sub-task plan (JSON with a subTasks array) found in the planning run output");
    }

    private static EpicDecompositionResult Finalize(List<EpicSubTaskSpec> specs)
        => specs.Count == 0
            ? new EpicDecompositionResult([], "the planning run's plan contained no sub-tasks with a title")
            : new EpicDecompositionResult(specs, null);

    /// <summary>
    /// True when <paramref name="json"/> parses and matches the expected shape
    /// (an object carrying a <c>subTasks</c> array, or a bare array of
    /// objects). On a match, <paramref name="specs"/> holds the non-blank-title
    /// entries; an empty list is still a match (the structure was present, it
    /// just had nothing usable in it).
    /// </summary>
    private static bool TryExtractSpecs(string json, out List<EpicSubTaskSpec> specs)
    {
        specs = [];
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json, DocOptions);
            var root = doc.RootElement;

            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
            }
            else if (root.ValueKind == JsonValueKind.Object
                     && TryGetProp(root, out var sub, "subTasks", "sub_tasks", "subtasks")
                     && sub.ValueKind == JsonValueKind.Array)
            {
                array = sub;
            }
            else
            {
                return false;
            }

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var title = GetString(item, "title", "name");
                if (string.IsNullOrWhiteSpace(title)) continue;
                specs.Add(new EpicSubTaskSpec(
                    Title: title!.Trim(),
                    PromptMarkdown: GetString(item, "promptMarkdown", "prompt", "promptMd", "body"),
                    CliType: GetString(item, "cliType", "cli"),
                    Model: GetString(item, "model")));
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IEnumerable<string> GreedyJsonCandidates(string text)
    {
        var obj = Regex.Match(text, @"\{[\s\S]*\}");
        if (obj.Success) yield return obj.Value;
        var arr = Regex.Match(text, @"\[[\s\S]*\]");
        if (arr.Success) yield return arr.Value;
    }

    private static bool TryGetProp(JsonElement obj, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out value)) return true;
            // Case-insensitive fallback so "SubTasks" / "Title" also resolve.
            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement obj, params string[] names)
    {
        if (!TryGetProp(obj, out var value, names)) return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        return null;
    }
}
