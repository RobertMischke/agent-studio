using System.Text.RegularExpressions;

namespace AgentStudio.CliHosting;

/// <summary>
/// Provider-account failures have host-wide consequences, so only a
/// distinguishable provider response may enter an authentication or limit
/// circuit. Ordinary tool failures stay with the run that produced them.
/// </summary>
public enum ProviderFailureKind
{
    None,
    SignedOut,
    RateLimited,
    Transient,
}

public sealed record ProviderFailureClassification(
    ProviderFailureKind Kind,
    string Detail);

/// <summary>Pure, CLI-neutral classification shared by local and remote runners.</summary>
public static partial class ProviderFailureClassifier
{
    private static readonly string[] SignedOutSignals =
    [
        "not logged in",
        "not signed in",
        "logged out",
        "no active session",
        "not authenticated",
        "no credentials",
        "login required",
        "please log in",
        "please login",
        "re-authenticate",
        "reauthenticate",
        "oauth token expired",
        "access token expired",
        "refresh token expired",
        "refresh token revoked",
        "invalid refresh token",
        "invalid api key",
        "missing bearer authentication",
        "chatgpt account authentication failed",
    ];

    private static readonly string[] HighConfidenceRunSignedOutSignals =
    [
        "not logged in",
        "not signed in",
        "login required",
        "please log in",
        "please login",
        "oauth token expired",
        "refresh token expired",
        "refresh token revoked",
        "invalid refresh token",
        "invalid api key",
        "chatgpt account authentication failed",
    ];

    private static readonly string[] ProviderProcessSignedOutSignals =
    [
        "missing bearer authentication",
        "missing bearer or basic authentication",
    ];

    private static readonly string[] LimitSignals =
    [
        "hit your session limit",
        "session limit",
        "session limit reached",
        "usage limit",
        "usage limit reached",
        "you've reached your usage limit",
        "quota exceeded",
        "rate limit exceeded",
        "rate_limit_exceeded",
        "insufficient_quota",
        "too many requests",
        "status=rejected",
        "· rejected ·",
    ];

    private static readonly string[] TransientSignals =
    [
        "timed out",
        "timeout",
        "connection reset",
        "connection refused",
        "connection closed",
        "network error",
        "network is unreachable",
        "temporary failure",
        "temporarily unavailable",
        "service unavailable",
        "failed to refresh token",
        "token refresh failed",
        "refresh in progress",
        "try again",
    ];

    public static ProviderFailureClassification Classify(
        string? provider,
        int exitCode,
        string? stdout,
        string? stderr)
    {
        if (exitCode == 0) return new(ProviderFailureKind.None, "process succeeded");
        var text = string.Join('\n', new[] { stderr, ErrorOutput(stdout) }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (IsRateLimit(text))
            return new(ProviderFailureKind.RateLimited, FirstMatchingLine(text, LimitSignals));
        if (IsDistinguishableSignedOut(provider, text))
            return new(ProviderFailureKind.SignedOut, FirstSignedOutLine(text));
        if (ContainsAny(text, TransientSignals))
            return new(ProviderFailureKind.Transient, FirstMatchingLine(text, TransientSignals));
        return new(ProviderFailureKind.None, "non-authentication process failure");
    }

    public static bool IsRateLimit(string? text)
        => ContainsAny(text, LimitSignals) || RateLimitStatusRegex().IsMatch(text ?? string.Empty);

    public static bool IsDistinguishableSignedOut(
        string? provider,
        string? text,
        bool trustedProviderStatus = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (LooksLikeGitAuthentication(text) || LooksLikeToolFailure(text)) return false;
        if (trustedProviderStatus && ContainsAny(text, SignedOutSignals)) return true;
        if (ContainsAny(text, HighConfidenceRunSignedOutSignals)) return true;
        if (!string.IsNullOrWhiteSpace(provider)
            && ContainsAny(text, ProviderProcessSignedOutSignals)) return true;

        // Bare 401/unauthorized strings occur in tool and repository output.
        // Accept them only when the same line names provider-login material.
        foreach (var line in Lines(text))
        {
            if (!UnauthorizedRegex().IsMatch(line)) continue;
            if (trustedProviderStatus) return true;
            if (!string.IsNullOrWhiteSpace(provider)
                && line.Contains(provider, StringComparison.OrdinalIgnoreCase)
                && LoginMaterialRegex().IsMatch(line))
                return true;
        }
        return false;
    }

    public static string FirstRateLimitLine(string? text)
        => FirstMatchingLine(text, LimitSignals);

    private static bool LooksLikeGitAuthentication(string text)
        => text.Contains("fatal: Authentication failed for http", StringComparison.OrdinalIgnoreCase)
           || text.Contains("github.com", StringComparison.OrdinalIgnoreCase)
              && text.Contains("authentication failed", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeToolFailure(string text)
        => text.Contains("apply_patch verification failed", StringComparison.OrdinalIgnoreCase)
           || text.Contains("failed to find context", StringComparison.OrdinalIgnoreCase)
           || text.Contains("context-not-found", StringComparison.OrdinalIgnoreCase)
           || text.Contains("patch apply", StringComparison.OrdinalIgnoreCase);

    private static string ErrorOutput(string? stdout)
        => string.Join('\n', Lines(stdout).Where(line => ErrorEnvelopeRegex().IsMatch(line)));

    private static string FirstSignedOutLine(string text)
    {
        foreach (var line in Lines(text))
        {
            if (ContainsAny(line, SignedOutSignals)
                || UnauthorizedRegex().IsMatch(line) && AuthContextRegex().IsMatch(line))
                return Bounded(line);
        }
        return "provider reported invalid credentials";
    }

    private static string FirstMatchingLine(string? text, IReadOnlyList<string> signals)
    {
        foreach (var line in Lines(text))
        {
            if (ContainsAny(line, signals) || RateLimitStatusRegex().IsMatch(line))
                return Bounded(line);
        }
        return "provider rejected the request";
    }

    private static IEnumerable<string> Lines(string? text)
        => (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim());

    private static bool ContainsAny(string? text, IReadOnlyList<string> signals)
        => !string.IsNullOrWhiteSpace(text)
           && signals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase));

    private static string Bounded(string value)
        => value.Length <= 300 ? value : value[..300];

    [GeneratedRegex(@"(?:^|\D)429(?:\D|$)", RegexOptions.CultureInvariant)]
    private static partial Regex RateLimitStatusRegex();

    [GeneratedRegex(@"\b(?:401|unauthorized)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnauthorizedRegex();

    [GeneratedRegex(@"\b(?:auth(?:entication)?|bearer|credentials?|login|oauth|access token|refresh token|api key)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthContextRegex();

    [GeneratedRegex(@"\b(?:credentials?|login|oauth|token|api key|account)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LoginMaterialRegex();

    [GeneratedRegex(@"(?:\berror\b|\bfailed\b|\bfailure\b|\bunauthorized\b|\binvalid\b|\bstatus\s*[:=]\s*[45]\d\d\b|""is_error""\s*:\s*true)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorEnvelopeRegex();
}
