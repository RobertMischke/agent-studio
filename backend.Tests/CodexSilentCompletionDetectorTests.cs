

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the per-tick Codex silent-completion detector
/// (<see cref="CodexSilentCompletionDetector"/>). Pure-function library;
/// these tests run in &lt;1 s with no CLI process required.
///
/// <para>
/// Bug class this guards: 2026-05-12 Lotta-dashboard redesign run. Codex
/// did all the work, ran one last <c>command_execution</c> with exit_code=0,
/// then stopped emitting frames - no <c>turn.completed</c>, no sentinel.
/// The phase-aware watchdog would have killed it after 600 s as a hard
/// failure; the user intervened manually. The detector recognises that
/// shape inside 90 s and finalizes the run as Completed instead.
/// </para>
/// </summary>
public class CodexSilentCompletionDetectorTests
{
    private static CodexSilentCompletionDetector.DetectionInputs Inputs(
        string cliType = CliTypes.Codex,
        RunPhase phase = RunPhase.TurnInProgress,
        double silenceSeconds = 90,
        int? exitCode = 0,
        string? command = "pwsh.exe -Command \".\\serve.ps1 status\"",
        string? outputTail = "ng serve laeuft nicht.\nFalse",
        bool alreadyTripped = false)
        => new(cliType, phase, silenceSeconds, exitCode, command, outputTail, alreadyTripped);

    [Fact]
    public void Trips_ForCodex_AfterSuccessfulCommandExecution_AndSilencePastThreshold()
    {
        // The canonical silent-completion shape from the live Lotta-dashboard
        // bug: Codex ran a successful sanity-check command, the runner phase
        // is back in TurnInProgress, no new frame for >60 s. Detector trips
        // with a diagnosis that echoes the trigger command + output tail.
        var verdict = CodexSilentCompletionDetector.Decide(Inputs());
        Assert.True(verdict.ShouldTrip);
        Assert.Contains("Codex stopped after final tool call", verdict.Diagnosis);
        Assert.Contains("silence=90s", verdict.Diagnosis);
        Assert.Contains("phase=TurnInProgress", verdict.Diagnosis);
        Assert.Contains("serve.ps1", verdict.Diagnosis);
        Assert.Contains("ng serve laeuft nicht", verdict.Diagnosis);
    }

    [Fact]
    public void Trips_AtExactlyDefaultSilenceThreshold()
    {
        // AC#5: synthetic Codex stream that goes stale ≥ 60 s after the
        // last frame must be recognised. Locks the threshold at the
        // documented default so a future tightening / widening is visible.
        var verdict = CodexSilentCompletionDetector.Decide(
            Inputs(silenceSeconds: CodexSilentCompletionDetector.DefaultSilenceSeconds));
        Assert.True(verdict.ShouldTrip);
    }

    [Fact]
    public void DoesNotTrip_BeforeSilenceThreshold()
    {
        // 59 s is short of the 60 s threshold; the realistic tool window
        // is still healthy at that point.
        var verdict = CodexSilentCompletionDetector.Decide(Inputs(silenceSeconds: 59));
        Assert.False(verdict.ShouldTrip);
    }

    [Fact]
    public void DoesNotTrip_WhenLastCommandFailed()
    {
        // A non-zero exit usually precedes another reasoning turn (Codex
        // wants to react). Treat as "agent still working" - the silent-
        // completion shape is specifically the post-success hang.
        var verdict = CodexSilentCompletionDetector.Decide(Inputs(exitCode: 1));
        Assert.False(verdict.ShouldTrip);
    }

    [Fact]
    public void DoesNotTrip_WhenNoCommandExecutionObservedYet()
    {
        var verdict = CodexSilentCompletionDetector.Decide(Inputs(exitCode: null));
        Assert.False(verdict.ShouldTrip);
    }

    [Theory]
    [InlineData(RunPhase.Spawning)]
    [InlineData(RunPhase.SessionInitializing)]
    [InlineData(RunPhase.PromptConsumed)]
    [InlineData(RunPhase.TurnCompleted)]
    [InlineData(RunPhase.TurnFailed)]
    [InlineData(RunPhase.NeedsInput)]
    [InlineData(RunPhase.Exited)]
    [InlineData(RunPhase.Killed)]
    [InlineData(RunPhase.Unknown)]
    public void DoesNotTrip_OutsideCandidatePhases(RunPhase phase)
    {
        var verdict = CodexSilentCompletionDetector.Decide(Inputs(phase: phase));
        Assert.False(verdict.ShouldTrip);
    }

    [Theory]
    [InlineData(RunPhase.TurnInProgress)]
    [InlineData(RunPhase.OutputDelta)]
    [InlineData(RunPhase.ToolExecuting)]
    public void Trips_InEveryCandidatePhase(RunPhase phase)
    {
        var verdict = CodexSilentCompletionDetector.Decide(Inputs(phase: phase));
        Assert.True(verdict.ShouldTrip);
    }

    [Theory]
    [InlineData(CliTypes.Claude)]
    [InlineData(CliTypes.Gemini)]
    [InlineData("")]
    public void DoesNotTrip_ForNonCodexCli(string cliType)
    {
        // Other CLIs have different completion contracts (claude-cli's
        // result frame is already handled by the SentinelDetected kill).
        var verdict = CodexSilentCompletionDetector.Decide(Inputs(cliType: cliType));
        Assert.False(verdict.ShouldTrip);
    }

    [Fact]
    public void DoesNotTrip_WhenAlreadyTripped()
    {
        // Latch: once the runner has fired the trip, further ticks must
        // not produce a second synthetic marker / second Stop call.
        var verdict = CodexSilentCompletionDetector.Decide(Inputs(alreadyTripped: true));
        Assert.False(verdict.ShouldTrip);
    }

    [Fact]
    public void Diagnosis_TruncatesLongCommandAndOutputForReadableChat()
    {
        // A very long command + output should still fit on one chat line.
        var bigCommand = new string('x', 500);
        var bigOutput = new string('y', 1000);
        var verdict = CodexSilentCompletionDetector.Decide(
            Inputs(command: bigCommand, outputTail: bigOutput));
        Assert.True(verdict.ShouldTrip);
        // Command capped at 120 chars + ellipsis; output capped at 200 + ellipsis.
        Assert.DoesNotContain(new string('x', 130), verdict.Diagnosis);
        Assert.DoesNotContain(new string('y', 210), verdict.Diagnosis);
        Assert.Contains("…", verdict.Diagnosis);
    }

    [Fact]
    public void Diagnosis_HandlesNullCommandAndOutput()
    {
        var verdict = CodexSilentCompletionDetector.Decide(
            Inputs(command: null, outputTail: null));
        Assert.True(verdict.ShouldTrip);
        Assert.Contains("<unknown command>", verdict.Diagnosis);
        Assert.Contains("(no output)", verdict.Diagnosis);
    }

    [Fact]
    public void CustomSilenceThresholdOverride_IsHonoured()
    {
        // Allows per-CLI calibration / tightening from tests without
        // recompiling the default. 30 s threshold + 45 s observed → trips.
        var verdict = CodexSilentCompletionDetector.Decide(
            Inputs(silenceSeconds: 45),
            silenceThresholdSeconds: 30);
        Assert.True(verdict.ShouldTrip);

        var verdict2 = CodexSilentCompletionDetector.Decide(
            Inputs(silenceSeconds: 45),
            silenceThresholdSeconds: 60);
        Assert.False(verdict2.ShouldTrip);
    }
}
