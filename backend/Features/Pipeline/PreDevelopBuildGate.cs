namespace AgentStudio.Pipeline;

/// <summary>
/// Build boundary in front of the integration branch. Where
/// <see cref="PreMainTestGate"/> guards the release branch with the mandatory
/// full suite BEFORE a fast-forward, this gate guards <c>develop</c> with the
/// cheap compile stage AFTER the merge commit exists: the interesting subject is
/// the MERGE RESULT (delivery + current integration tip), which no earlier gate
/// has ever built. The full test evidence for the card itself was already
/// produced by the auto-review gate, so this stage deliberately runs
/// <see cref="TestExecutionLevels.BuildOnly"/> - build / lint commands, no test
/// commands, no continuous baseline.
///
/// <para>
/// Convention instead of a settings switch: the gate applies exactly when the
/// project declares build commands in its <see cref="BuildProfile"/>
/// (<see cref="AppliesTo"/>). A project without one merges exactly as before.
/// The verification itself always runs against the exact merged SHA in an
/// isolated worktree (<c>RequireExactSubject</c>), so the live checkout is never
/// used as a command workspace.
/// </para>
/// </summary>
public sealed class PreDevelopBuildGate
{
    private readonly IBuildTestGateRunner _runner;

    public PreDevelopBuildGate(IBuildTestGateRunner runner) => _runner = runner;

    /// <summary>
    /// True when the project's build profile yields build commands, i.e. there is
    /// something deterministic to compile. Without one the merge stays ungated
    /// (and the runner logs that it skipped the gate) rather than inventing a
    /// build command for a repo whose layout we only guessed at.
    /// </summary>
    public static bool AppliesTo(BuildProfile? profile)
        => VerifyCommandPlanner.HasProfileBuildCommands(profile);

    /// <summary>
    /// Builds <paramref name="request"/>'s exact subject. The test level is
    /// overwritten with <see cref="TestExecutionLevels.BuildOnly"/> and the
    /// subject is pinned, so neither caller input nor lane configuration can turn
    /// this into a full suite (cost) or into a HEAD-drifting run (dishonest
    /// evidence).
    /// </summary>
    public Task<BuildTestGateResult> RunAsync(
        BuildTestGateRequest request,
        BuildProfile? profile,
        TimeSpan timeout,
        CancellationToken ct)
        => _runner.RunAsync(
            request with
            {
                RequireExactSubject = true,
                RequiredTestLevel = TestExecutionLevels.BuildOnly,
            },
            changedFiles: null,
            profile,
            PostStepMode.Fail,
            timeout,
            ct);

    /// <summary>
    /// The gate is green on <see cref="BuildTestGateVerdict.Ok"/> and on
    /// <see cref="BuildTestGateVerdict.NotApplicable"/> (nothing derivable to
    /// build, so it must not invent a blocker). A skipped run is not green:
    /// where commands exist, an unverified merge never stays on the integration
    /// branch. Infrastructure failures remain fail-closed as well.
    /// </summary>
    public static bool IsGreen(BuildTestGateResult result)
        => result.Verdict is BuildTestGateVerdict.Ok or BuildTestGateVerdict.NotApplicable;
}
