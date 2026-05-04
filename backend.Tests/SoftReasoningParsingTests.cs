using OrchestratorApi.Services.Supervisor;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the parser for soft-reasoning sentinels. The CLI wrapping these
/// observations is replaceable, but the sentinel grammar is the contract
/// between supervisor prompt template and the orchestrator's storage. Same
/// shape as the hard agent contract sentinels.
/// </summary>
public class SoftReasoningParsingTests
{
    private static readonly DateTime At = new(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Parse_ExtractsSingleObservation_WithSeverityTopicMessage()
    {
        const string output = "Some narrative...\n[[SUPERVISOR_OBSERVATION: severity=warn; topic=prompt-scope-drift; message=The agent is editing files outside the task scope.]]\n[[TASK_DONE]]\n";
        var advisories = SoftReasoningParsing.Parse(output, "p", At, "j");
        Assert.Single(advisories);
        var a = advisories[0];
        Assert.Equal(SupervisorSeverity.Warn, a.Severity);
        Assert.Equal("prompt-scope-drift", a.Topic);
        Assert.Equal("The agent is editing files outside the task scope.", a.Message);
        Assert.Equal(SupervisorSource.SoftReasoning, a.Source);
        Assert.Equal("j", a.JobId);
    }

    [Fact]
    public void Parse_ExtractsMultipleObservations()
    {
        const string output = @"
[[SUPERVISOR_OBSERVATION: severity=info; topic=on-track; message=Tool calls match the prompt.]]
[[SUPERVISOR_OBSERVATION: severity=high; topic=quota-burn-trajectory; message=At current rate quota will hit 100% in 12 minutes.]]
[[TASK_DONE]]";
        var advisories = SoftReasoningParsing.Parse(output, "p", At, "j");
        Assert.Equal(2, advisories.Count);
        Assert.Equal(SupervisorSeverity.Info, advisories[0].Severity);
        Assert.Equal(SupervisorSeverity.High, advisories[1].Severity);
    }

    [Fact]
    public void Parse_ReturnsEmpty_OnNoSentinels()
    {
        Assert.Empty(SoftReasoningParsing.Parse("nothing here", "p", At, "j"));
        Assert.Empty(SoftReasoningParsing.Parse("", "p", At, "j"));
        Assert.Empty(SoftReasoningParsing.Parse(null!, "p", At, "j"));
    }

    [Fact]
    public void Parse_DefaultsToInfoSeverity_WhenMissing()
    {
        const string output = "[[SUPERVISOR_OBSERVATION: topic=t; message=m]]";
        var advisories = SoftReasoningParsing.Parse(output, "p", At, null);
        Assert.Single(advisories);
        Assert.Equal(SupervisorSeverity.Info, advisories[0].Severity);
    }

    [Fact]
    public void Parse_TolerantOfFieldOrderAndCase()
    {
        const string output = "[[supervisor_observation: MESSAGE=keep going; SEVERITY=Warn; TOPIC=on-track]]";
        var advisories = SoftReasoningParsing.Parse(output, "p", At, null);
        Assert.Single(advisories);
        Assert.Equal(SupervisorSeverity.Warn, advisories[0].Severity);
        Assert.Equal("on-track", advisories[0].Topic);
        Assert.Equal("keep going", advisories[0].Message);
    }

    [Fact]
    public void Parse_SkipsObservationsWithoutMessage()
    {
        const string output = "[[SUPERVISOR_OBSERVATION: severity=warn; topic=t]]";
        Assert.Empty(SoftReasoningParsing.Parse(output, "p", At, null));
    }

    [Fact]
    public void Parse_TreatsUnknownSeverityAsInfo()
    {
        const string output = "[[SUPERVISOR_OBSERVATION: severity=panic; topic=t; message=m]]";
        var advisories = SoftReasoningParsing.Parse(output, "p", At, null);
        Assert.Single(advisories);
        Assert.Equal(SupervisorSeverity.Info, advisories[0].Severity);
    }
}
