using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>The kind of a dry-run step, for the timeline label / logs.</summary>
public enum DryRunStepKind
{
    Install,
    Build,
}

/// <summary>One ordered command in the validation dry-run.</summary>
public sealed record DryRunStep(DryRunStepKind Kind, string Command);

/// <summary>
/// Pure planner (Slice P / ASS-1663) that turns a <see cref="BuildProfile"/> into
/// the ordered command list of the validation dry-run: the install command (if
/// any) first, then every build command in declared order. Deterministic and
/// side-effect-free so the ordering is unit-testable without running anything.
///
/// <para>
/// Test commands are intentionally NOT part of the dry-run: the onboarding gate
/// is "install + build green", which is the cheap, deterministic check that the
/// stack can be set up in a fresh worktree. Running the full test suite belongs
/// to a later verify post-step, not the onboarding gate.
/// </para>
/// </summary>
public static class BuildProfileDryRunPlanner
{
    public static IReadOnlyList<DryRunStep> Plan(BuildProfile? profile)
    {
        var steps = new List<DryRunStep>();
        if (profile is null) return steps;

        if (!string.IsNullOrWhiteSpace(profile.InstallCmd))
            steps.Add(new DryRunStep(DryRunStepKind.Install, profile.InstallCmd.Trim()));

        foreach (var build in profile.BuildCmds ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(build))
                steps.Add(new DryRunStep(DryRunStepKind.Build, build.Trim()));

        return steps;
    }
}
