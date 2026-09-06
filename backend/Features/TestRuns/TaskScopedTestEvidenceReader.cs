using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.TestRuns;

/// <summary>
/// Reads deterministic test evidence that is already attached to a task
/// folder. Remote Review build-tests grades and build/test gate logs predate
/// the project-wide TestRunStore, so card projection must include both sources
/// instead of treating the absence of a project run as the absence of evidence.
/// </summary>
internal static class TaskScopedTestEvidenceReader
{
    private static readonly (string Pattern, string Kind, string Label)[] GatePatterns =
    [
        ("build-test-gate-*.log", "build-test-gate", "Build/test gate"),
        ("pre-develop-build-gate-*.log", "pre-develop-build-gate", "Pre-develop build gate"),
        ("pre-main-test-gate-*.log", "pre-main-test-gate", "Pre-main test gate"),
    ];

    public static TaskScopedTestEvidenceSnapshot Read(TaskInfo task)
    {
        if (string.IsNullOrWhiteSpace(task.FolderPath) || !Directory.Exists(task.FolderPath))
            return new([], "missing");

        var sources = new List<TaskTestEvidenceSource>();
        var signature = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         task.FolderPath,
                         "remote-review-grade-*.md",
                         SearchOption.TopDirectoryOnly))
            {
                AddSignature(path, signature);
                sources.AddRange(ReadRemoteReview(path));
            }

            var postSteps = Path.Combine(task.FolderPath, "post-steps");
            if (Directory.Exists(postSteps))
            {
                foreach (var (pattern, kind, label) in GatePatterns)
                {
                    foreach (var path in Directory.EnumerateFiles(postSteps, pattern, SearchOption.TopDirectoryOnly))
                    {
                        AddSignature(path, signature);
                        if (ReadGate(path, kind, label) is { } gate) sources.Add(gate);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            signature.Add(ex.GetType().Name);
        }

        return new(
            sources.OrderByDescending(source => source.ObservedAt ?? DateTime.MinValue).ToList(),
            string.Join('|', signature.OrderBy(value => value, StringComparer.Ordinal)));
    }

    private static IReadOnlyList<TaskTestEvidenceSource> ReadRemoteReview(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        var frontmatter = ReadFrontmatter(text);
        if (!string.Equals(frontmatter.GetValueOrDefault("type"), "remote-review-grade", StringComparison.OrdinalIgnoreCase))
            return [];

        var rows = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("|", StringComparison.Ordinal))
            .Select(ParseTableRow)
            .ToList();
        var commit = frontmatter.GetValueOrDefault("actualHead")
                     ?? frontmatter.GetValueOrDefault("expectedResultSha")
                     ?? "";
        if (string.IsNullOrWhiteSpace(commit)) return [];

        var observedAt = ParseDate(frontmatter.GetValueOrDefault("receivedAt"))
                         ?? File.GetLastWriteTimeUtc(path);
        var reportRef = Path.GetFileName(path);
        var attemptId = frontmatter.GetValueOrDefault("attemptId") ?? Path.GetFileNameWithoutExtension(path);
        var sources = new List<TaskTestEvidenceSource>();
        var buildRows = rows
            .Where(columns => columns.Count >= 2
                              && string.Equals(columns[0], "build-tests", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var verificationCommands = rows
            .Where(columns => columns.Count >= 3
                              && string.Equals(columns[0], "verification", StringComparison.OrdinalIgnoreCase))
            .Select(columns => columns[2])
            .Where(value => !string.IsNullOrWhiteSpace(value) && value != "---")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var namedBuildCommands = buildRows
            .SelectMany(columns => columns.Count >= 4 ? CommandNames(columns[3]) : [])
            .Concat(verificationCommands)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var failedBuildRows = buildRows.Where(columns => !IsPass(columns[1])).ToList();
        var passed = buildRows.Count > 0
                     && failedBuildRows.Count == 0
                     && buildRows.All(columns => IsPass(columns[1]));
        var result = buildRows.Count == 0 ? "not-proven" : passed ? "passed" : "failed";
        var resultLabel = result switch
        {
            "passed" => "Pass",
            "failed" => "Failed",
            _ => "Not proven",
        };
        var commandSuffix = namedBuildCommands.Count > 0 && result != "not-proven"
            ? $" ({string.Join(", ", namedBuildCommands)})"
            : "";
        var buildReason = result switch
        {
            "passed" => EnsureSentence($"{FormatList(namedBuildCommands, "All recorded build-test commands")} passed"),
            "failed" => FailureReason(failedBuildRows.Count > 0 ? failedBuildRows : buildRows),
            _ when verificationCommands.Count > 0 => EnsureSentence(
                $"Build-tests verdict missing for {FormatList(verificationCommands)}"),
            _ => "Build-tests verdict missing because the report records no verification command.",
        };
        sources.Add(new TaskTestEvidenceSource
        {
            Kind = "review-build-tests",
            Id = attemptId,
            Commit = commit,
            Result = result,
            ObservedAt = observedAt,
            Summary = $"Review build-tests {resultLabel} at {Short(commit)}{commandSuffix}",
            Reason = buildReason,
            ReportRef = reportRef,
        });

        foreach (var columns in rows.Where(columns =>
                     columns.Count >= 4
                     && !string.Equals(columns[0], "build-tests", StringComparison.OrdinalIgnoreCase)
                     && IsBlocking(columns[1])))
        {
            var aspect = columns[0];
            var detail = string.IsNullOrWhiteSpace(columns[3])
                ? "The review report did not provide a summary."
                : columns[3];
            sources.Add(new TaskTestEvidenceSource
            {
                Kind = "review-aspects",
                Id = $"{attemptId}:{aspect}",
                Commit = commit,
                Result = "blocked",
                ObservedAt = observedAt,
                Summary = $"Review blocked by {aspect}",
                Reason = EnsureSentence($"{aspect} blocked: {detail.Trim().TrimEnd('.')}"),
                ReportRef = reportRef,
            });
        }

        return sources;
    }

    private static TaskTestEvidenceSource? ReadGate(string path, string kind, string label)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        var verdict = ReadToken(text, "verdict");
        var reason = ReadLineValue(text, "reason");
        var commit = ReadToken(text, "testedSha");
        if (string.IsNullOrWhiteSpace(commit) || commit.Equals("n/a", StringComparison.OrdinalIgnoreCase))
            commit = ReadToken(text, "expectedSha");
        if (string.IsNullOrWhiteSpace(verdict)
            || string.IsNullOrWhiteSpace(commit)
            || commit.Equals("n/a", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalizedVerdict = verdict.ToLowerInvariant();
        var legacyNotApplicable = normalizedVerdict == "skipped"
                                  && string.Equals(
                                      reason,
                                      "no verify commands derivable",
                                      StringComparison.OrdinalIgnoreCase);
        var result = normalizedVerdict switch
        {
            "ok" or "warn" => "passed",
            "fail" => "failed",
            "notapplicable" or "not-applicable" => "not-applicable",
            "skipped" when legacyNotApplicable => "not-applicable",
            _ => "not-proven",
        };
        var resultLabel = result switch
        {
            "passed" => "green",
            "failed" => "failed",
            "not-applicable" => "not applicable",
            _ => "skipped",
        };
        var observedAt = ParseDate(ReadToken(text, "completedAtUtc"))
                         ?? File.GetLastWriteTimeUtc(path);
        var id = ReadToken(text, "gateRunId");
        if (string.IsNullOrWhiteSpace(id) || id.Equals("n/a", StringComparison.OrdinalIgnoreCase))
            id = Path.GetFileNameWithoutExtension(path);

        return new TaskTestEvidenceSource
        {
            Kind = kind,
            Id = id,
            Commit = commit,
            Result = result,
            ObservedAt = observedAt,
            Summary = result == "not-applicable" && kind == "build-test-gate"
                ? "No build/test defined"
                : $"{label} {resultLabel} at {Short(commit)}",
            Reason = EnsureSentence(string.IsNullOrWhiteSpace(reason)
                ? $"{label} did not record a reason"
                : reason),
            ReportRef = $"post-steps/{Path.GetFileName(path)}",
        };
    }

    private static bool IsPass(string value) =>
        value.Equals("pass", StringComparison.OrdinalIgnoreCase)
        || value.Equals("passed", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlocking(string value) =>
        value.Equals("block", StringComparison.OrdinalIgnoreCase)
        || value.Equals("blocked", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> CommandNames(string summary) =>
        Regex.Matches(summary, "`([^`]+)`")
            .Select(match => match.Groups[1].Value.Trim())
            .Where(value => value.Length > 0)
            .ToList();

    private static string FailureReason(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var detail = rows
            .Select(columns => columns.Count >= 4 ? columns[3].Trim() : "")
            .FirstOrDefault(value => value.Length > 0);
        return EnsureSentence(string.IsNullOrWhiteSpace(detail)
            ? "A build-tests verdict failed"
            : detail);
    }

    private static string FormatList(IReadOnlyList<string> values, string fallback = "") => values.Count switch
    {
        0 => fallback,
        1 => values[0],
        2 => $"{values[0]} and {values[1]}",
        _ => $"{string.Join(", ", values.Take(values.Count - 1))}, and {values[^1]}",
    };

    private static string EnsureSentence(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return "No reason was recorded.";
        return trimmed[^1] is '.' or '!' or '?' ? trimmed : trimmed + ".";
    }

    private static Dictionary<string, string> ReadFrontmatter(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(text);
        if (!string.Equals(reader.ReadLine()?.Trim(), "---", StringComparison.Ordinal)) return values;
        while (reader.ReadLine() is { } line)
        {
            if (string.Equals(line.Trim(), "---", StringComparison.Ordinal)) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            values[line[..colon].Trim()] = Unquote(line[(colon + 1)..].Trim());
        }
        return values;
    }

    private static IReadOnlyList<string> ParseTableRow(string line) =>
        line.Trim('|').Split('|').Select(value => value.Trim()).ToList();

    private static string? ReadToken(string text, string key)
    {
        var marker = key + "=";
        foreach (var line in text.Split('\n'))
        {
            var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var value = line[(index + marker.Length)..];
            var end = value.IndexOfAny([' ', '\r', '\n']);
            return (end >= 0 ? value[..end] : value).Trim();
        }
        return null;
    }

    private static string? ReadLineValue(string text, string key)
    {
        var marker = key + "=";
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;
            return line[marker.Length..].Trim();
        }
        return null;
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal)
            : value;

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    private static void AddSignature(string path, ICollection<string> signature)
    {
        var info = new FileInfo(path);
        signature.Add($"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
    }
}

internal sealed record TaskScopedTestEvidenceSnapshot(
    IReadOnlyList<TaskTestEvidenceSource> Sources,
    string Signature);
