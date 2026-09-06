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

        var verdicts = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("|", StringComparison.Ordinal))
            .Select(ParseTableRow)
            .Where(columns => columns.Count >= 4
                              && !string.Equals(columns[0], "Aspect", StringComparison.OrdinalIgnoreCase)
                              && !string.Equals(columns[0], "---", StringComparison.Ordinal))
            .ToList();
        var commit = frontmatter.GetValueOrDefault("actualHead")
                     ?? frontmatter.GetValueOrDefault("expectedResultSha")
                     ?? "";
        if (string.IsNullOrWhiteSpace(commit)) return [];

        var observedAt = ParseDate(frontmatter.GetValueOrDefault("receivedAt"))
                         ?? File.GetLastWriteTimeUtc(path);
        var attemptId = frontmatter.GetValueOrDefault("attemptId") ?? Path.GetFileNameWithoutExtension(path);
        var reportRef = Path.GetFileName(path);
        var sources = new List<TaskTestEvidenceSource>();
        var buildRows = verdicts
            .Where(columns => string.Equals(columns[0], "build-tests", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var result = BuildTestResult(buildRows);
        var resultLabel = result switch
        {
            "passed" => "Pass",
            "failed" => "Failed",
            "not-applicable" => "Not applicable",
            _ => "Not proven",
        };
        var commands = buildRows
            .Select(columns => CommandName(columns[3]))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var commandSuffix = commands.Count > 0 ? $" ({string.Join(", ", commands)})" : "";
        sources.Add(new TaskTestEvidenceSource
        {
            Kind = "review-build-tests",
            Id = attemptId,
            Commit = commit,
            Result = result,
            ObservedAt = observedAt,
            Summary = $"Review build-tests {resultLabel} at {Short(commit)}{commandSuffix}",
            Reason = BuildTestReason(buildRows, commands, result),
            ReportRef = reportRef,
        });

        foreach (var blocked in verdicts.Where(columns => IsBlocked(columns[1])
                                                          && !string.Equals(columns[0], "build-tests", StringComparison.OrdinalIgnoreCase)))
        {
            var aspect = blocked[0];
            var aspectSummary = blocked[3].Trim().TrimEnd('.');
            sources.Add(new TaskTestEvidenceSource
            {
                Kind = "review-aspects",
                Id = $"{attemptId}:{aspect}",
                Commit = commit,
                Result = "blocked",
                ObservedAt = observedAt,
                Summary = $"Review blocked by {aspect}",
                Reason = $"{aspect} blocked: {aspectSummary}",
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
            Reason = string.IsNullOrWhiteSpace(reason) ? GateFallbackReason(result) : reason.Trim(),
            ReportRef = Path.Combine("post-steps", Path.GetFileName(path)).Replace('\\', '/'),
        };
    }

    private static string BuildTestResult(IReadOnlyList<IReadOnlyList<string>> buildRows)
    {
        if (buildRows.Count == 0) return "not-proven";
        if (buildRows.All(columns => IsNotApplicable(columns[1], columns[2])))
            return "not-applicable";
        if (buildRows.All(columns => columns[1].Equals("pass", StringComparison.OrdinalIgnoreCase)
                                     || IsNotApplicable(columns[1], columns[2])))
            return "passed";
        return "failed";
    }

    private static string BuildTestReason(
        IReadOnlyList<IReadOnlyList<string>> buildRows,
        IReadOnlyList<string> commands,
        string result)
    {
        if (buildRows.Count == 0)
            return "build-tests missing: No build-tests command row exists in the review report.";
        if (result == "passed")
            return commands.Count switch
            {
                0 => "All build-tests rows passed.",
                1 => $"{commands[0]} passed.",
                2 => $"{commands[0]} and {commands[1]} passed.",
                _ => $"{string.Join(", ", commands.Take(commands.Count - 1))}, and {commands[^1]} passed.",
            };
        if (result == "not-applicable")
            return "The review report marks every build-tests command as not applicable.";

        var failed = buildRows.First(columns => !columns[1].Equals("pass", StringComparison.OrdinalIgnoreCase));
        var summary = failed[3].Trim().TrimEnd('.');
        return $"build-tests failed: {summary}.";
    }

    private static string? CommandName(string summary)
    {
        var quoteStart = summary.IndexOf('\'');
        if (quoteStart < 0) return null;
        var quoteEnd = summary.IndexOf('\'', quoteStart + 1);
        return quoteEnd > quoteStart + 1 ? summary[(quoteStart + 1)..quoteEnd].Trim() : null;
    }

    private static bool IsBlocked(string status) =>
        status.Equals("block", StringComparison.OrdinalIgnoreCase)
        || status.Equals("blocked", StringComparison.OrdinalIgnoreCase);

    private static bool IsNotApplicable(string status, string classification) =>
        status.Equals("not-applicable", StringComparison.OrdinalIgnoreCase)
        || status.Equals("not_applicable", StringComparison.OrdinalIgnoreCase)
        || (status.Equals("skipped", StringComparison.OrdinalIgnoreCase)
            && classification.Equals("NoCommands", StringComparison.OrdinalIgnoreCase));

    private static string GateFallbackReason(string result) => result switch
    {
        "passed" => "All selected build/test commands passed.",
        "failed" => "At least one selected build/test command failed.",
        "not-applicable" => "No build/test commands are defined.",
        _ => "The build/test gate has no completed command proof.",
    };

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
