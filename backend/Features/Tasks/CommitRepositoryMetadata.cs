using System.Text.RegularExpressions;
using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>
/// Compatibility helpers for repository-aware commit attribution. Message
/// prefixes remain display text; only records without structured repository
/// metadata may consult them, so a migrated record is never reinterpreted.
/// </summary>
public static partial class CommitRepositoryMetadata
{
    [GeneratedRegex(@"^\[(?<repository>[^\]\r\n]+)\](?:\s+|$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex LegacyRepositoryPrefix();

    public static string? LegacyRepositoryFromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var match = LegacyRepositoryPrefix().Match(message);
        return match.Success ? match.Groups["repository"].Value.Trim() : null;
    }

    public static string Label(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) return "repository";
        var value = repository.Trim().TrimEnd('/', '\\');
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile)
            value = uri.AbsolutePath.TrimEnd('/');
        var split = Math.Max(value.LastIndexOf('/'), Math.Max(value.LastIndexOf('\\'), value.LastIndexOf(':')));
        var label = split >= 0 ? value[(split + 1)..] : value;
        return label.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? label[..^4]
            : label;
    }

    public static bool Same(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        var a = Normalize(left);
        var b = Normalize(right);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
               || string.Equals(Label(a), Label(b), StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<TaskCommitInfo> ReadPersistedCommits(string taskFolder)
    {
        try
        {
            var path = Path.Combine(taskFolder, "task.json");
            if (!File.Exists(path)) return [];
            var root = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path), TaskJsonFile.ReadOpts);
            if (root.ValueKind != JsonValueKind.Object) return [];
            var result = new List<TaskCommitInfo>();
            if (root.TryGetProperty("commits", out var commits) && commits.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in commits.EnumerateArray())
                {
                    var commit = item.Deserialize<TaskCommitInfo>(TaskJsonFile.ReadOpts);
                    if (commit is not null && !string.IsNullOrWhiteSpace(commit.Sha)) result.Add(commit);
                }
                return result;
            }
            if (root.TryGetProperty("commit", out var singular) && singular.ValueKind == JsonValueKind.Object)
            {
                var commit = singular.Deserialize<TaskCommitInfo>(TaskJsonFile.ReadOpts);
                if (commit is not null && !string.IsNullOrWhiteSpace(commit.Sha)) result.Add(commit);
            }
            return result;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "CommitRepositoryMetadata: persisted commit metadata is best-effort");
            return [];
        }
    }

    private static string Normalize(string value)
        => value.Trim().Replace('\\', '/').TrimEnd('/');
}
