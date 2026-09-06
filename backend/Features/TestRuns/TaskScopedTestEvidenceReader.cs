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
            .Select(columns => new ReviewVerdict(columns[0], columns[1], columns[2], columns[3]))
            .ToList();
        var commit = frontmatter.GetValueOrDefault("actualHead")
                     ?? frontmatter.GetValueOrDefault("expectedResultSha")
                     ?? "";
        if (string.IsNullOrWhiteSpace(commit)) return [];

        var observedAt = ParseDate(frontmatter.GetValueOrDefault("receivedAt"))
                         ?? File.GetLastWriteTimeUtc(path);
        var attemptId = frontmatter.GetValueOrDefault("attemptId") ?? Path.GetFileNameWithoutExtension(path);
        var reportRef = Path.GetFileName(path);
        var commands = ReadReviewCommandNames(text);
        var buildVerdicts = verdicts
            .Where(verdict => string.Equals(verdict.Aspect, "build-tests", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var failed = buildVerdicts.Any(verdict => !verdict.Status.Equals("pass", StringComparison.OrdinalIgnoreCase));
        var result = buildVerdicts.Count == 0 ? "not-proven" : failed ? "failed" : "passed";
        var resultLabel = result switch
        {
            "passed" => "Pass",
            "failed" => "Failed",
            _ => "Not proven",
        };
        var commandSuffix = commands.Count == 0 ? "" : $" ({string.Join(", ", commands)})";
        var buildReason = BuildTestsReason(result, buildVerdicts, commands);
        var sources = new List<TaskTestEvidenceSource>
        {
            new()
            {
                Kind = "review-build-tests",
                Id = attemptId,
                Commit = commit,
                Result = result,
                ObservedAt = observedAt,
                Summary = $"Review build-tests {resultLabel} at {Short(commit)}{commandSuffix}",
                Reason = buildReason,
                ReportRef = reportRef,
            },
        };

        var blockedAspects = verdicts
            .Where(verdict => !string.Equals(verdict.Aspect, "build-tests", StringComparison.OrdinalIgnoreCase)
                              && IsBlocking(verdict.Status))
            .ToList();
        if (blockedAspects.Count > 0)
        {
            var aspectNames = string.Join(", ", blockedAspects.Select(verdict => verdict.Aspect));
            sources.Add(new TaskTestEvidenceSource
            {
                Kind = "review-aspects",
                Id = attemptId,
                Commit = commit,
                Result = "blocked",
                ObservedAt = observedAt,
                Summary = $"Review blocked by {aspectNames}",
                Reason = BuildBlockedAspectsReason(blockedAspects),
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
            Reason = string.IsNullOrWhiteSpace(reason)
                ? $"{label} recorded verdict {verdict}."
                : EnsureSentence(reason),
            ReportRef = $"post-steps/{Path.GetFileName(path)}",
        };
    }

    private static IReadOnlyList<string> ReadReviewCommandNames(string text) => text.Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.StartsWith("|", StringComparison.Ordinal))
        .Select(ParseTableRow)
        .Where(columns => columns.Count >= 3
                          && string.Equals(columns[0], "verification", StringComparison.OrdinalIgnoreCase)
                          && columns[2].StartsWith("verify-", StringComparison.OrdinalIgnoreCase))
        .Select(columns => columns[2])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string BuildTestsReason(
        string result,
        IReadOnlyList<ReviewVerdict> verdicts,
        IReadOnlyList<string> commands)
    {
        if (result == "not-proven")
        {
            return commands.Count == 0
                ? "The build-tests command is missing from the remote review report."
                : $"The build-tests row is missing for {JoinNames(commands)}.";
        }

        if (result == "passed" && commands.Count > 0)
            return $"{JoinNames(commands)} passed.";

        var relevant = result == "failed"
            ? verdicts.Where(verdict => !verdict.Status.Equals("pass", StringComparison.OrdinalIgnoreCase)).ToList()
            : verdicts.ToList();
        var summaries = relevant
            .Select(verdict => verdict.Summary.Trim())
            .Where(summary => !string.IsNullOrWhiteSpace(summary))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (summaries.Count > 0)
        {
            var prefix = result == "failed" ? "build-tests failed: " : "build-tests passed: ";
            return EnsureSentence(prefix + string.Join("; ", summaries.Select(InlineClause)));
        }

        var state = result == "failed" ? "failed" : "passed";
        return commands.Count == 0
            ? $"The remote review build-tests {state}."
            : $"{JoinNames(commands)} {state}.";
    }

    private static string BuildBlockedAspectsReason(IReadOnlyList<ReviewVerdict> verdicts) =>
        EnsureSentence(string.Join(
            "; ",
            verdicts.Select(verdict => $"{verdict.Aspect} blocked: {InlineClause(verdict.Summary)}")));

    private static string InlineClause(string value) => value.Trim().TrimEnd('.', '!', '?');

    private static bool IsBlocking(string status) => status.Equals("block", StringComparison.OrdinalIgnoreCase)
        || status.Equals("blocked", StringComparison.OrdinalIgnoreCase)
        || status.Equals("fail", StringComparison.OrdinalIgnoreCase)
        || status.Equals("failed", StringComparison.OrdinalIgnoreCase);

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "The verification command",
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))}, and {names[^1]}",
    };

    private static string EnsureSentence(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "No reason was recorded.";
        return ".!?".Contains(trimmed[^1]) ? trimmed : trimmed + ".";
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

    private sealed record ReviewVerdict(string Aspect, string Status, string Classification, string Summary);
}

internal sealed record TaskScopedTestEvidenceSnapshot(
    IReadOnlyList<TaskTestEvidenceSource> Sources,
    string Signature);
