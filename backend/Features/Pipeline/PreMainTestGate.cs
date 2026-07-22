namespace AgentStudio.Pipeline;

/// <summary>
/// Hard release boundary for a future/connected merge-to-main workflow. This
/// wrapper overwrites both lane configuration and caller input with
/// <c>full</c>; no diff, Test Hub signal, or LLM answer can reduce the suite.
/// The current product has no write path that merges develop into main, so this
/// contract is the required entry point for that operation when connected.
/// </summary>
public sealed class PreMainTestGate
{
    private readonly IBuildTestGateRunner _runner;

    public PreMainTestGate(IBuildTestGateRunner runner) => _runner = runner;

    public Task<BuildTestGateResult> RunAsync(
        BuildTestGateRequest request,
        BuildProfile? profile,
        TimeSpan timeout,
        CancellationToken ct)
        => _runner.RunAsync(
            request with { RequiredTestLevel = TestExecutionLevels.Full },
            changedFiles: null,
            profile,
            PostStepMode.Fail,
            timeout,
            ct);
}
