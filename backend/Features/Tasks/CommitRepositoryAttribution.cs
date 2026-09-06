using System.Text.RegularExpressions;

namespace AgentStudio.Tasks;

/// <summary>
/// Compatibility parser for the historical informational <c>[repo]</c> commit
/// subject prefix. New code must read <see cref="TaskCommitInfo.Repository"/>;
/// this parser exists only to migrate old task records once.
/// </summary>
public static partial class CommitRepositoryAttribution
{
    [GeneratedRegex(@"^\s*\[(?<repository>[^\]\r\n]+)\]\s*",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();

    public static string? ParseLegacyPrefix(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var match = PrefixPattern().Match(message);
        return match.Success ? match.Groups["repository"].Value.Trim() : null;
    }

    public static string DisplayName(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) return "repository";
        var value = repository.Trim().TrimEnd('/', '\\');
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            value = uri.AbsolutePath.TrimEnd('/');
        var separator = Math.Max(value.LastIndexOf('/'), Math.Max(value.LastIndexOf('\\'), value.LastIndexOf(':')));
        var name = separator >= 0 ? value[(separator + 1)..] : value;
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public static bool SameName(string? left, string? right)
        => Normalize(left) == Normalize(right);

    private static string Normalize(string? value)
        => string.Concat(DisplayName(value).Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
