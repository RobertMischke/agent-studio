using AgentStudio.HostHealth;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix over <see cref="LocalCliInstallDiagnosis"/>. The card's
/// central requirement is one row of this table: a missing shim with the
/// package still present must be repairable, while a truly uninstalled CLI
/// must never trigger an automatic global npm install.
/// </summary>
public class LocalCliInstallDiagnosisTests
{
    private static LocalCliInstallFacts Facts(
        bool probeOk = false,
        bool binResolved = true,
        bool shim = false,
        bool orphans = false,
        bool package = true,
        string? packageVersion = "2.1.234",
        string? probedVersion = null)
        => new()
        {
            CliType = "claude",
            PackageId = "@anthropic-ai/claude-code",
            VersionProbeOk = probeOk,
            ProbedVersion = probedVersion,
            NpmGlobalBinResolved = binResolved,
            ShimPresent = shim,
            OrphanShimsPresent = orphans,
            PackagePresent = package,
            PackageVersion = packageVersion,
        };

    public static TheoryData<string, LocalCliInstallFacts, LocalCliInstallState, LocalCliRepairAction> Matrix() => new()
    {
        {
            "a working CLI is ready whatever the install looks like",
            Facts(probeOk: true, shim: false, package: false, probedVersion: "2.1.234"),
            LocalCliInstallState.Ready, LocalCliRepairAction.None
        },
        {
            "no npm global bin means we cannot tell, so we escalate instead of guessing",
            Facts(binResolved: false),
            LocalCliInstallState.Unknown, LocalCliRepairAction.EscalateToOperator
        },
        {
            "no package and no shim is a deliberate state, never an automatic install",
            Facts(package: false, shim: false, packageVersion: null),
            LocalCliInstallState.NotInstalled, LocalCliRepairAction.EscalateToOperator
        },
        {
            "a missing package outranks a stray shim; still no automatic install",
            Facts(package: false, shim: true, packageVersion: null),
            LocalCliInstallState.NotInstalled, LocalCliRepairAction.EscalateToOperator
        },
        {
            "atomic-rename orphans stay with the existing shim repair",
            Facts(orphans: true, shim: false),
            LocalCliInstallState.PackageBroken, LocalCliRepairAction.RestoreShims
        },
        {
            "orphans win over the missing shim: renaming back is cheaper than reinstalling",
            Facts(orphans: true, shim: false, package: true),
            LocalCliInstallState.PackageBroken, LocalCliRepairAction.RestoreShims
        },
        {
            "AGT-2673: package present, shims gone, nothing to rename back",
            Facts(shim: false, orphans: false, package: true),
            LocalCliInstallState.ShimMissingPackagePresent, LocalCliRepairAction.GlobalReinstall
        },
        {
            "shim present but the binary will not run is the half-installed-stub shape",
            Facts(shim: true, orphans: false, package: true),
            LocalCliInstallState.PackageBroken, LocalCliRepairAction.RestoreShims
        },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Diagnose_maps_facts_to_state_and_action(
        string because,
        LocalCliInstallFacts facts,
        LocalCliInstallState expectedState,
        LocalCliRepairAction expectedAction)
    {
        var result = LocalCliInstallDiagnosis.Diagnose(facts);

        Assert.Equal(expectedState, result.State);
        Assert.Equal(expectedAction, result.Action);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary), $"missing summary for: {because}");
    }

    [Fact]
    public void GlobalReinstall_is_licensed_only_for_the_missing_shim_shape()
    {
        var licensed = Matrix()
            .Select(row => (
                State: (LocalCliInstallState)row[2],
                Action: (LocalCliRepairAction)row[3]))
            .Where(row => row.Action == LocalCliRepairAction.GlobalReinstall)
            .Select(row => row.State)
            .Distinct()
            .ToList();

        Assert.Equal([LocalCliInstallState.ShimMissingPackagePresent], licensed);
    }

    [Fact]
    public void Summary_for_the_missing_shim_shape_names_the_package_and_its_installed_version()
    {
        var result = LocalCliInstallDiagnosis.Diagnose(Facts(shim: false, packageVersion: "2.1.234"));

        Assert.Contains("@anthropic-ai/claude-code@2.1.234", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_stays_readable_when_the_installed_version_is_unreadable()
    {
        var result = LocalCliInstallDiagnosis.Diagnose(Facts(shim: false, packageVersion: null));

        Assert.Contains("@anthropic-ai/claude-code is still installed", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("@@", result.Summary, StringComparison.Ordinal);
    }
}
