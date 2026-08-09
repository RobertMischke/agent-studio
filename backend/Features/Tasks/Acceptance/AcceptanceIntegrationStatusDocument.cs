using System.Collections.Concurrent;
using System.Text;

namespace AgentStudio.Tasks;

/// <summary>
/// Owns the bounded acceptance-integration section in status.md. The markers
/// allow retries to replace stale reasons while preserving the task result.
/// </summary>
public static class AcceptanceIntegrationStatusDocument
{
    internal const string StartMarker = "<!-- agent-studio:acceptance-integration:start -->";
    internal const string EndMarker = "<!-- agent-studio:acceptance-integration:end -->";
    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    public static void WriteFailure(
        string folderPath,
        string outcome,
        string? detail,
        string integrationBranch,
        DateTime? recordedAtUtc = null)
    {
        var section = new StringBuilder()
            .AppendLine(StartMarker)
            .AppendLine("## Acceptance integration")
            .AppendLine()
            .AppendLine($"- Outcome: `{SingleLine(outcome)}`")
            .AppendLine($"- Lane: `{TaskStates.HumanReview}`")
            .AppendLine($"- Integration branch: `{SingleLine(integrationBranch)}`")
            .AppendLine($"- Reason: {SingleLine(detail, "Integration did not complete.")}")
            .AppendLine($"- Recorded at: `{(recordedAtUtc ?? DateTime.UtcNow):O}`")
            .AppendLine(EndMarker)
            .ToString();
        Upsert(folderPath, section);
    }

    public static void WriteOperatorOverride(
        string folderPath,
        string? reason,
        DateTime? recordedAtUtc = null)
    {
        var section = new StringBuilder()
            .AppendLine(StartMarker)
            .AppendLine("## Acceptance integration")
            .AppendLine()
            .AppendLine("- Outcome: `OperatorOverride`")
            .AppendLine($"- Lane: `{TaskStates.Completed}`")
            .AppendLine("- Integration: explicitly waived by the operator")
            .AppendLine($"- Reason: {SingleLine(reason, "No reason supplied.")}")
            .AppendLine($"- Recorded at: `{(recordedAtUtc ?? DateTime.UtcNow):O}`")
            .AppendLine(EndMarker)
            .ToString();
        Upsert(folderPath, section);
    }

    public static void Clear(string folderPath)
    {
        var path = Path.Combine(folderPath, "status.md");
        lock (PathLocks.GetOrAdd(path, static _ => new object()))
        {
            if (!File.Exists(path)) return;
            var original = File.ReadAllText(path);
            var updated = RemoveOwnedSection(original).TrimEnd();
            if (string.Equals(original.TrimEnd(), updated, StringComparison.Ordinal)) return;
            ReplaceAtomically(path, updated.Length == 0 ? string.Empty : updated + Environment.NewLine);
        }
    }

    private static void Upsert(string folderPath, string section)
    {
        Directory.CreateDirectory(folderPath);
        var path = Path.Combine(folderPath, "status.md");
        lock (PathLocks.GetOrAdd(path, static _ => new object()))
        {
            var original = File.Exists(path) ? File.ReadAllText(path) : "# Result\n";
            var preserved = RemoveOwnedSection(original).TrimEnd();
            var updated = preserved.Length == 0
                ? section
                : preserved + Environment.NewLine + Environment.NewLine + section;
            ReplaceAtomically(path, updated.TrimEnd() + Environment.NewLine);
        }
    }

    private static string RemoveOwnedSection(string content)
    {
        var start = content.IndexOf(StartMarker, StringComparison.Ordinal);
        if (start < 0) return content;
        var end = content.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (end < 0) return content[..start];
        return content.Remove(start, end + EndMarker.Length - start);
    }

    private static void ReplaceAtomically(string path, string content)
    {
        var tempPath = path + ".acceptance-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static string SingleLine(string? value, string fallback = "")
    {
        var normalized = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return normalized.Length == 0 ? fallback : normalized;
    }
}
