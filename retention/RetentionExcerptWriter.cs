using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Retention;

public sealed record RetentionExcerpt(string RelativePath, string Markdown);

public sealed partial class RetentionExcerptWriter
{
    private const int HeadLines = 200;
    private const int TailLines = 500;
    private const int ErrorRadius = 50;

    public async Task<IReadOnlyList<RetentionExcerpt>> CreateAsync(
        string taskRoot,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        var excerpts = new List<RetentionExcerpt>();
        var results = relativePaths
            .Where(path => Normalize(path).StartsWith("results/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (results.Length > 0)
            excerpts.Add(await CreateResultsExcerptAsync(taskRoot, results, cancellationToken));

        foreach (var relativePath in relativePaths.Where(path =>
                     !Normalize(path).StartsWith("results/", StringComparison.OrdinalIgnoreCase)))
        {
            var normalized = Normalize(relativePath);
            var fullPath = SafePath(taskRoot, normalized);
            if (!File.Exists(fullPath))
                continue;
            if (normalized.Contains("cli-output.log", StringComparison.OrdinalIgnoreCase))
                excerpts.Add(await CreateCliExcerptAsync(fullPath, normalized, cancellationToken));
            else if (Path.GetFileName(normalized).Contains("review", StringComparison.OrdinalIgnoreCase))
                excerpts.Add(await CreateReviewExcerptAsync(fullPath, normalized, cancellationToken));
            else
                excerpts.Add(await CreateInventoryEntryAsync(fullPath, normalized, cancellationToken));
        }
        return excerpts;
    }

    private static async Task<RetentionExcerpt> CreateCliExcerptAsync(
        string fullPath,
        string source,
        CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
        var errors = ErrorLineRegex();
        var indices = lines.Select((line, index) => (line, index))
            .Where(item => errors.IsMatch(item.line))
            .Select(item => item.index)
            .ToArray();
        var errorWindowIndices = indices
            .SelectMany(index => Enumerable.Range(
                Math.Max(0, index - ErrorRadius),
                Math.Min(lines.Length, index + ErrorRadius + 1) - Math.Max(0, index - ErrorRadius)))
            .Distinct()
            .Order()
            .ToArray();
        var commands = lines.Where(line => CommandLineRegex().IsMatch(line)).Distinct().ToArray();
        var timestamps = lines.Where(line => TimestampRegex().IsMatch(line)).ToArray();
        var content = new StringBuilder();
        Begin(content, source, new FileInfo(fullPath).Length);
        content.AppendLine($"- Lines: {lines.Length}");
        content.AppendLine($"- Error matches: {indices.Length}");
        content.AppendLine($"- Tool calls: {lines.Count(line => ToolCallRegex().IsMatch(line))}");
        content.AppendLine($"- Test runs: {lines.Count(line => TestRegex().IsMatch(line))}");
        content.AppendLine($"- Commits: {lines.Count(line => CommitRegex().IsMatch(line))}");
        content.AppendLine($"- Token lines: {lines.Count(line => TokenRegex().IsMatch(line))}");
        content.AppendLine();
        Section(content, "Timestamps and duration", timestamps);
        Section(content, "Commands", commands);
        Section(content, "Head", lines.Take(HeadLines));
        Section(content, "Error windows", errorWindowIndices.Select(index => $"{index + 1}: {lines[index]}"));
        Section(content, "Tail", lines.TakeLast(TailLines));
        return new RetentionExcerpt(ExcerptPath(source), content.ToString());
    }

    private static async Task<RetentionExcerpt> CreateReviewExcerptAsync(
        string fullPath,
        string source,
        CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
        var selected = lines.Where(line => ReviewLineRegex().IsMatch(line)).ToArray();
        var content = new StringBuilder();
        Begin(content, source, new FileInfo(fullPath).Length);
        content.AppendLine($"- Matching verdict or finding lines: {selected.Length}");
        content.AppendLine();
        Section(content, "Timestamps and duration", lines.Where(line => TimestampRegex().IsMatch(line)));
        Section(content, "Commands", []);
        Section(content, "Head", []);
        Section(content, "Error windows", selected);
        Section(content, "Tail", []);
        return new RetentionExcerpt(ExcerptPath(source), content.ToString());
    }

    private static async Task<RetentionExcerpt> CreateResultsExcerptAsync(
        string taskRoot,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        Begin(content, "results/", paths.Sum(path => new FileInfo(SafePath(taskRoot, path)).Length));
        content.AppendLine($"- Files: {paths.Count}");
        content.AppendLine();
        Section(content, "Timestamps and duration", []);
        Section(content, "Commands", []);
        content.AppendLine("## Head");
        content.AppendLine();
        content.AppendLine("| Path | Bytes | SHA-256 |");
        content.AppendLine("|---|---:|---|");
        foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = SafePath(taskRoot, path);
            await using var stream = File.OpenRead(fullPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            content.AppendLine($"| `{Normalize(path)}` | {stream.Length} | `{hash}` |");
        }
        content.AppendLine();
        content.AppendLine("## Error windows");
        content.AppendLine();
        foreach (var path in paths.Where(IsFullResult))
        {
            content.AppendLine($"### {Normalize(path)}");
            content.AppendLine();
            content.AppendLine(await File.ReadAllTextAsync(SafePath(taskRoot, path), cancellationToken));
            content.AppendLine();
        }
        Section(content, "Tail", []);
        return new RetentionExcerpt("excerpts/results.excerpt.md", content.ToString());
    }

    private static async Task<RetentionExcerpt> CreateInventoryEntryAsync(
        string fullPath,
        string source,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(fullPath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        var content = new StringBuilder();
        Begin(content, source, stream.Length);
        content.AppendLine($"- SHA-256: `{hash}`");
        content.AppendLine();
        foreach (var heading in new[] { "Timestamps and duration", "Commands", "Head", "Error windows", "Tail" })
            Section(content, heading, []);
        return new RetentionExcerpt(ExcerptPath(source), content.ToString());
    }

    private static void Begin(StringBuilder content, string source, long bytes)
    {
        content.AppendLine("# Retention excerpt");
        content.AppendLine();
        content.AppendLine("## Source");
        content.AppendLine();
        content.AppendLine($"- Path: `{Normalize(source)}`");
        content.AppendLine($"- Original bytes: {bytes}");
        content.AppendLine();
        content.AppendLine("## Summary");
        content.AppendLine();
    }

    private static void Section(StringBuilder content, string heading, IEnumerable<string> lines)
    {
        content.AppendLine($"## {heading}");
        content.AppendLine();
        content.AppendLine("```text");
        foreach (var line in lines)
            content.AppendLine(line);
        content.AppendLine("```");
        content.AppendLine();
    }

    private static bool IsFullResult(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var file = Path.GetFileName(path);
        return extension == ".md"
            || file.Contains("report", StringComparison.OrdinalIgnoreCase)
               && extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".zip");
    }

    private static string ExcerptPath(string source)
    {
        var safe = Normalize(source).Replace('/', '-').Replace('\\', '-');
        return $"excerpts/{safe}.excerpt.md";
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string SafePath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, Normalize(relativePath)));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Artifact path escapes task root: {relativePath}");
        return path;
    }

    [GeneratedRegex(@"error|exception|failed|exit(?: code|=|:)?\s*[1-9]\d*|non[- ]zero exit", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLineRegex();
    [GeneratedRegex(@"(?:^|\s)(?:\$|>|command(?:=|:)|exec(?:ute|uting)?(?:=|:))\s*.+", RegexOptions.IgnoreCase)]
    private static partial Regex CommandLineRegex();
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ][0-9:.+-]+|duration|elapsed", RegexOptions.IgnoreCase)]
    private static partial Regex TimestampRegex();
    [GeneratedRegex(@"tool(?: call|_call| use)", RegexOptions.IgnoreCase)]
    private static partial Regex ToolCallRegex();
    [GeneratedRegex(@"\b(?:dotnet test|npm test|pytest|playwright|tests? (?:passed|failed|run))\b", RegexOptions.IgnoreCase)]
    private static partial Regex TestRegex();
    [GeneratedRegex(@"\b(?:git commit|commit(?:ted)?\s+[0-9a-f]{7,40})\b", RegexOptions.IgnoreCase)]
    private static partial Regex CommitRegex();
    [GeneratedRegex(@"\btokens?\b", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();
    [GeneratedRegex(@"verdict|finding|severity|pass(?:ed)?|warn(?:ing)?|fail(?:ed|ure)?|block(?:ed|er)?", RegexOptions.IgnoreCase)]
    private static partial Regex ReviewLineRegex();
}
