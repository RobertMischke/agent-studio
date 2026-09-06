using System.Globalization;

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

        var verdicts = ReadVerdictRows(text);
        var commit = frontmatter.GetValueOrDefault("actualHead")
                     ?? frontmatter.GetValueOrDefault("expectedResultSha")
                     ?? "";
        if (string.IsNullOrWhiteSpace(commit)) return [];

        var observedAt = ParseDate(frontmatter.GetValueOrDefault("receivedAt"))
                         ?? File.GetLastWriteTimeUtc(path);
        var attemptId = frontmatter.GetValueOrDefault("attemptId") ?? Path.GetFileNameWithoutExtension(path);
        var reportRef = Path.GetFileName(path);
        var buildVerdicts = verdicts
            .Where(row => string.Equals(row.Aspect, "build-tests", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var buildSteps = BuildStepIds(text, buildVerdicts);
        var buildFailed = buildVerdicts.Count > 0
                          && buildVerdicts.Any(row => !row.Status.Equals("pass", StringComparison.OrdinalIgnoreCase));
        var buildPassed = buildVerdicts.Count > 0
                          && buildVerdicts.All(row => row.Status.Equals("pass", StringComparison.OrdinalIgnoreCase));
        var result = buildFailed ? "failed" : buildPassed ? "passed" : "not-proven";
        var resultLabel = result switch
        {
            "passed" => "Pass",
            "failed" => "Failed",
            _ => "Not proven",
        };
        var stepSuffix = buildSteps.Count == 0 ? "" : $" ({string.Join(", ", buildSteps)})";
        var buildReason = BuildReason(result, buildVerdicts, buildSteps);
        var sources = new List<TaskTestEvidenceSource>
        {
            new()
            {
                Kind = "review-build-tests",
                Id = attemptId,
                Commit = commit,
                Result = result,
                ObservedAt = observedAt,
                Summary = $"Review build-tests {resultLabel} at {Short(commit)}{stepSuffix}",
                Reason = buildReason,
                ReportRef = reportRef,
            },
        };

        sources.AddRange(verdicts
            .Where(row => !string.Equals(row.Aspect, "build-tests", StringComparison.OrdinalIgnoreCase)
                          && IsBlocking(row.Status))
            .Select(row => new TaskTestEvidenceSource
            {
                Kind = "review-aspects",
                Id = $"{attemptId}:{row.Aspect}",
                Commit = commit,
                Result = "blocked",
                ObservedAt = observedAt,
                Summary = $"Review blocked by {row.Aspect}",
                Reason = $"{row.Aspect} blocked: {Sentence(row.Summary)}",
                ReportRef = reportRef,
            }));

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
            Reason = string.IsNullOrWhiteSpace(reason)
                ? $"{label} reported {resultLabel}."
                : Sentence(reason),
            ReportRef = $"post-steps/{Path.GetFileName(path)}",
        };
    }

    private static IReadOnlyList<string> BuildStepIds(
        string text,
        IReadOnlyList<ReviewVerdictRow> buildVerdicts)
    {
        var ids = new List<string>();
        foreach (var verdict in buildVerdicts)
        {
            const string marker = "Review command '";
            var start = verdict.Summary.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) continue;
            start += marker.Length;
            var end = verdict.Summary.IndexOf('\'', start);
            if (end > start) ids.Add(verdict.Summary[start..end]);
        }

        if (ids.Count == 0)
        {
            ids.AddRange(text.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("|", StringComparison.Ordinal))
                .Select(ParseTableRow)
                .Where(columns => columns.Count >= 3
                                  && string.Equals(columns[0], "verification", StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(columns[1], "candidate", StringComparison.OrdinalIgnoreCase)
                                  && columns[2].StartsWith("verify-", StringComparison.OrdinalIgnoreCase))
                .Select(columns => columns[2]));
        }

        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string BuildReason(
        string result,
        IReadOnlyList<ReviewVerdictRow> verdicts,
        IReadOnlyList<string> stepIds)
    {
        if (result == "passed")
            return stepIds.Count == 0
                ? "All reported build-tests rows passed."
                : $"{JoinSteps(stepIds)} passed.";

        if (result == "failed")
        {
            var failed = verdicts.First(row => !row.Status.Equals("pass", StringComparison.OrdinalIgnoreCase));
            return $"build-tests failed: {Sentence(failed.Summary)}";
        }

        return stepIds.Count == 0
            ? "Build-tests proof is missing because no build-tests row or verification command was reported."
            : $"The build-tests row is missing for {JoinSteps(stepIds)}.";
    }

    private static string JoinSteps(IReadOnlyList<string> stepIds) => stepIds.Count switch
    {
        0 => "verification commands",
        1 => stepIds[0],
        2 => $"{stepIds[0]} and {stepIds[1]}",
        _ => string.Join(", ", stepIds.Take(stepIds.Count - 1)) + $", and {stepIds[^1]}",
    };

    private static bool IsBlocking(string status) => status.Equals("fail", StringComparison.OrdinalIgnoreCase)
                                                     || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                                                     || status.Equals("block", StringComparison.OrdinalIgnoreCase)
                                                     || status.Equals("blocked", StringComparison.OrdinalIgnoreCase)
                                                     || status.Equals("concerns", StringComparison.OrdinalIgnoreCase);

    private static string Sentence(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return "No reason was supplied.";
        return trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?')
            ? trimmed
            : trimmed + ".";
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

    private static IReadOnlyList<ReviewVerdictRow> ReadVerdictRows(string text)
    {
        var rows = new List<ReviewVerdictRow>();
        var inVerdicts = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Equals("## Aspect verdicts", StringComparison.OrdinalIgnoreCase))
            {
                inVerdicts = true;
                continue;
            }
            if (!inVerdicts && line.StartsWith('|'))
            {
                var header = ParseTableRow(line);
                if (header.Count >= 2
                    && string.Equals(header[0], "Aspect", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(header[1], "Status", StringComparison.OrdinalIgnoreCase))
                {
                    inVerdicts = true;
                }
                continue;
            }
            if (inVerdicts && line.StartsWith("## ", StringComparison.Ordinal)) break;
            if (!inVerdicts || !line.StartsWith('|')) continue;

            var columns = ParseTableRow(line);
            if (columns.Count < 4
                || string.Equals(columns[0], "Aspect", StringComparison.OrdinalIgnoreCase)
                || columns[0].StartsWith("---", StringComparison.Ordinal))
                continue;
            rows.Add(new ReviewVerdictRow(columns[0], columns[1], columns[3]));
        }
        return rows;
    }

    private static IReadOnlyList<string> ParseTableRow(string line)
    {
        var trimmed = line.Trim().Trim('|');
        var columns = new List<string>();
        var cell = new System.Text.StringBuilder();
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
            {
                cell.Append('|');
                i++;
                continue;
            }
            if (trimmed[i] == '|')
            {
                columns.Add(cell.ToString().Trim());
                cell.Clear();
                continue;
            }
            cell.Append(trimmed[i]);
        }
        columns.Add(cell.ToString().Trim());
        return columns;
    }

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

    private sealed record ReviewVerdictRow(
        string Aspect,
        string Status,
        string Summary);
}

internal sealed record TaskScopedTestEvidenceSnapshot(
    IReadOnlyList<TaskTestEvidenceSource> Sources,
    string Signature);
