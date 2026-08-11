using AgentStudio.Runner;
using Contract = AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AttemptReAdoptionPolicyTests
{
    public static TheoryData<string, bool, bool, bool, bool, bool, string> Matrix => new()
    {
        { Contract.RunnerAttemptKinds.Review, true, true, true, true, true, "Adopt" },
        { "other", true, true, true, true, true, "RejectInvalidKind" },
        { Contract.RunnerAttemptKinds.Review, false, true, true, true, true, "RejectUnknownAttempt" },
        { Contract.RunnerAttemptKinds.Review, true, false, true, true, true, "RejectNotCurrent" },
        { Contract.RunnerAttemptKinds.Review, true, true, false, true, true, "RejectTerminal" },
        { Contract.RunnerAttemptKinds.Review, true, true, true, false, true, "RejectAuthorityMismatch" },
        { Contract.RunnerAttemptKinds.Review, true, true, true, true, false, "RejectAuthorityMismatch" },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Decide_requires_exact_current_fenced_authority(
        string kind,
        bool exists,
        bool current,
        bool leased,
        bool fence,
        bool instance,
        string expected)
        => Assert.Equal(
            expected,
            AttemptReAdoptionPolicy.Decide(Facts(kind, exists, current, leased, fence, instance)).ToString());

    private static AttemptReAdoptionFacts Facts(
        string kind = Contract.RunnerAttemptKinds.Review,
        bool exists = true,
        bool current = true,
        bool leased = true,
        bool fence = true,
        bool instance = true)
        => new(
            kind,
            exists,
            current,
            leased,
            HasLease: true,
            TaskMatches: true,
            ExecutorMatches: true,
            LeaseMatches: true,
            FenceMatches: fence,
            EpochMatches: true,
            InstanceMatches: instance);
}
