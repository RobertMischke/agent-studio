using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

public enum ProviderAuthFailureKind
{
    None,
    SignedOut,
    RateLimited,
    Transient,
    Indeterminate,
}

public sealed record ProviderAuthFailureEvidence(
    ProviderAuthFailureKind Kind,
    string Detail,
    DateTimeOffset? RetryAt = null);

/// <summary>
/// One provider-failure classifier shared by status probes and completed runs.
/// Rate limits and transient refresh/network failures take precedence over auth
/// phrases so an exit code alone can never turn into a sign-in alarm.
/// </summary>
public static class ProviderAuthFailureClassifier
{
    private static readonly Regex ExplicitSignedOut = new(
        """(?:not\s+(?:logged|signed)\s+in|logged\s+out|no\s+(?:active\s+)?session|not\s+authenticated|no\s+(?:stored\s+)?credentials|login\s+required|please\s+log\s*in|please\s+sign\s+in|re-?authenticate|invalid\s+(?:credentials|api\s*key)|refresh\s+token\s+(?:is\s+)?(?:expired|revoked))""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AuthRelated = new(
        """(?:\b401\b|unauthori[sz]ed|authentication\s+(?:failed|required)|invalid_grant|token\s+refresh|refresh(?:ing)?\s+(?:the\s+)?token|access\s+token)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Transient = new(
        """(?:timed?\s*out|timeout|temporar(?:y|ily)|try\s+again|connection\s+(?:reset|refused|closed)|network\s+(?:error|unavailable)|dns|econnreset|etimedout|eai_again|socket|tls\s+handshake|service\s+unavailable|bad\s+gateway|gateway\s+timeout|\b50[234]\b|refresh\s+(?:already\s+)?in\s+progress|token\s+refresh\s+race|(?:oauth|access)\s+token\s+(?:is\s+)?expired|token\s+has\s+expired)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ProviderAuthFailureEvidence Classify(
        ProcessResult result,
        DateTimeOffset observedAt,
        bool acceptExplicitZeroExitSignals = false)
    {
        if (result.ExitCode == 0 && !acceptExplicitZeroExitSignals)
            return new ProviderAuthFailureEvidence(ProviderAuthFailureKind.None, "Process completed successfully.");

        var text = $"{result.StdErr}\n{result.StdOut}";
        var limit = ProviderLimitParser.Parse(text, observedAt);
        if (limit.Limited)
            return new ProviderAuthFailureEvidence(
                ProviderAuthFailureKind.RateLimited,
                limit.Detail,
                limit.ResetAt);

        if (ExplicitSignedOut.IsMatch(text))
            return new ProviderAuthFailureEvidence(
                ProviderAuthFailureKind.SignedOut,
                "The provider explicitly reported no usable credentials.");

        if (Transient.IsMatch(text))
            return new ProviderAuthFailureEvidence(
                ProviderAuthFailureKind.Transient,
                "Transient provider authentication or connectivity error; retrying.");

        if (AuthRelated.IsMatch(text))
            return new ProviderAuthFailureEvidence(
                ProviderAuthFailureKind.Indeterminate,
                "Provider authentication failed without proof that the stored login is invalid.");

        if (result.ExitCode == 0)
            return new ProviderAuthFailureEvidence(ProviderAuthFailureKind.None, "Process completed successfully.");

        return new ProviderAuthFailureEvidence(
            ProviderAuthFailureKind.None,
            "No provider-authentication signal was present.");
    }
}
