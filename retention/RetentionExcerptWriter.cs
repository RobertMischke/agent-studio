using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Retention;

public static partial class RetentionExcerptWriter
{
    private const int HeadLines = 200;
    private const int TailLines = 500;
    private const int ErrorRadius = 50;

    public static string Write(string relativePath, ReadOnlySpan<byte> content)
    {
        var normalized = ArtifactClassifier.Normalize(relativePath);
        if (normalized.Contains("/results/", StringComparison.Ordinal) || normalized.StartsWith("results/", StringComparison.Ordinal))
            return WriteResultFile(relativePath, content);

        var text = Decode(content);
        if (normalized.Contains("review", StringComparison.Ordinal))
            return WriteReviewLog(relativePath, text);
        return WriteCliLog(relativePath, text);
    }

    public static string WriteResultsInventory(IEnumerable<(string Path, long Size, string Sha256)> files)
    {
        var builder = Header("results/");
        builder.AppendLine("## Inventory");
        builder.AppendLine();
        builder.AppendLine("| Path | Bytes | SHA-256 |");
        builder.AppendLine("| --- | ---: | --- |");
        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
            builder.AppendLine($"| `{Escape(file.Path)}` | {file.Size} | `{file.Sha256}` |");
        AppendEmptySections(builder, "Full content", "Signals", "Commands");
        return builder.ToString();
    }

    private static string WriteCliLog(string path, string text)
    {
        var lines = SplitLines(text);
        var selected = new SortedSet<int>();
        for (var index = 0; index < Math.Min(HeadLines, lines.Length); index++) selected.Add(index);
        for (var index = Math.Max(0, lines.Length - TailLines); index < lines.Length; index++) selected.Add(index);
        for (var index = 0; index < lines.Length; index++)
        {
            if (!ErrorLine().IsMatch(lines[index])) continue;
            for (var window = Math.Max(0, index - ErrorRadius); window <= Math.Min(lines.Length - 1, index + ErrorRadius); window++)
                selected.Add(window);
        }

        var commands = lines.Where(line => CommandLine().IsMatch(line)).Distinct(StringComparer.Ordinal).ToList();
        var timestamps = lines.Where(line => TimestampOrDuration().IsMatch(line)).Distinct(StringComparer.Ordinal).ToList();
        var builder = Header(path);
        builder.AppendLine("## Excerpt");
        builder.AppendLine();
        AppendSelected(builder, lines, selected);
        builder.AppendLine();
        builder.AppendLine("## Error windows");
        builder.AppendLine();
        foreach (var index in selected.Where(index => ErrorLine().IsMatch(lines[index])))
            builder.AppendLine($"- line {index + 1}: `{Escape(lines[index])}`");
        builder.AppendLine();
        builder.AppendLine("## Timing");
        builder.AppendLine();
        foreach (var line in timestamps) builder.AppendLine($"- `{Escape(line)}`");
        if (timestamps.Count == 0) builder.AppendLine("No timing lines detected.");
        builder.AppendLine();
        builder.AppendLine("## Counters");
        builder.AppendLine();
        builder.AppendLine($"- Tool calls: {lines.Count(line => ToolCall().IsMatch(line))}");
        builder.AppendLine($"- Test runs: {lines.Count(line => TestRun().IsMatch(line))}");
        builder.AppendLine($"- Commits: {lines.Count(line => CommitLine().IsMatch(line))}");
        builder.AppendLine($"- Token lines: {lines.Count(line => TokenLine().IsMatch(line))}");
        builder.AppendLine();
        builder.AppendLine("## Commands");
        builder.AppendLine();
        foreach (var command in commands) builder.AppendLine($"- `{Escape(command)}`");
        if (commands.Count == 0) builder.AppendLine("No commands detected.");
        return builder.ToString();
    }

    private static string WriteReviewLog(string path, string text)
    {
        var lines = SplitLines(text);
        var findings = lines.Where(line => ReviewSignal().IsMatch(line)).ToList();
        var builder = Header(path);
        builder.AppendLine("## Verdict and findings");
        builder.AppendLine();
        foreach (var line in findings) builder.AppendLine(line);
        if (findings.Count == 0) builder.AppendLine("No verdict or finding lines detected.");
        AppendEmptySections(builder, "Timing", "Counters", "Commands");
        return builder.ToString();
    }

    private static string WriteResultFile(string path, ReadOnlySpan<byte> content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var extension = Path.GetExtension(path);
        var keepFull = extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                       || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                       || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                       || extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
                       || path.Contains("report", StringComparison.OrdinalIgnoreCase);
        var builder = Header(path);
        builder.AppendLine("## Inventory");
        builder.AppendLine();
        builder.AppendLine($"- Bytes: {content.Length}");
        builder.AppendLine($"- SHA-256: `{hash}`");
        builder.AppendLine();
        builder.AppendLine("## Full content");
        builder.AppendLine();
        builder.AppendLine(keepFull ? Decode(content) : "Binary trace or image retained in cold storage only.");
        AppendEmptySections(builder, "Signals", "Commands");
        return builder.ToString();
    }

    private static StringBuilder Header(string path)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Retention excerpt");
        builder.AppendLine();
        builder.AppendLine($"Source: `{Escape(path)}`");
        builder.AppendLine();
        return builder;
    }

    private static void AppendSelected(StringBuilder builder, string[] lines, IEnumerable<int> selected)
    {
        builder.AppendLine("```text");
        foreach (var index in selected) builder.AppendLine(lines[index]);
        builder.AppendLine("```");
    }

    private static void AppendEmptySections(StringBuilder builder, params string[] headings)
    {
        foreach (var heading in headings)
        {
            builder.AppendLine();
            builder.AppendLine($"## {heading}");
            builder.AppendLine();
            builder.AppendLine("Not applicable.");
        }
    }

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    private static string Decode(ReadOnlySpan<byte> content) => Encoding.UTF8.GetString(content);
    private static string Escape(string value) => value.Replace("`", "'").Replace("|", "\\|");

    [GeneratedRegex(@"(?i)\b(error|exception|failed|exit(?: code)?\s*[:=]?\s*[1-9]\d*)\b")]
    private static partial Regex ErrorLine();
    [GeneratedRegex(@"(?i)^\s*(?:[$>]|command\s*:|exec(?:ute|uting)?\s*:|run\s*:)\s*.+")]
    private static partial Regex CommandLine();
    [GeneratedRegex(@"(?i)(\d{4}-\d{2}-\d{2}[t ]\d{2}:\d{2}|duration|elapsed|started at|finished at)")]
    private static partial Regex TimestampOrDuration();
    [GeneratedRegex(@"(?i)(tool[_ -]?call|calling tool|function call)")]
    private static partial Regex ToolCall();
    [GeneratedRegex(@"(?i)(dotnet test|npm test|playwright|pytest|test run)")]
    private static partial Regex TestRun();
    [GeneratedRegex(@"(?i)\b(commit|committed|git commit)\b")]
    private static partial Regex CommitLine();
    [GeneratedRegex(@"(?i)\b(tokens?|input_tokens|output_tokens)\b")]
    private static partial Regex TokenLine();
    [GeneratedRegex(@"(?i)\b(verdict|finding|severity|approve|approved|reject|reissue|blocker|critical|warning)\b")]
    private static partial Regex ReviewSignal();
}
