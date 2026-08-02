namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Canonical Git ref identity for one fenced RunAttempt result. The run id
/// separates attempts, the fence separates lease generations of that attempt,
/// and the result SHA makes the published target immutable.
/// </summary>
public static class FencedGitRefs
{
    public static string ImmutableResult(
        string sourceRunAttemptId,
        long fence,
        string resultSha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRunAttemptId);
        if (fence <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(fence),
                "A positive fence is required for an immutable result ref.");
        if (resultSha is not { Length: 40 or 64 }
            || resultSha.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Result SHA must be a 40- or 64-character hexadecimal identity.",
                nameof(resultSha));
        }

        return $"refs/heads/agent-studio/results/" +
               $"{RequireRefSegment(sourceRunAttemptId, nameof(sourceRunAttemptId))}/" +
               $"fence-{fence}/{resultSha.ToLowerInvariant()}";
    }

    private static string RequireRefSegment(string value, string parameterName)
    {
        var segment = value.Trim();
        if (segment is "." or ".."
            || segment.StartsWith('.')
            || segment.EndsWith('.')
            || segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("..", StringComparison.Ordinal)
            || segment.Any(character =>
                !(char.IsLetterOrDigit(character)
                  || character is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                "Run attempt ID contains characters that are unsafe in a Git ref segment.",
                parameterName);
        }
        return segment;
    }
}
