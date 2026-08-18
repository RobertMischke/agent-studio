namespace AgentStudio.HostHealth;

/// <summary>
/// Everything the host filesystem and a single <c>--version</c> probe can say
/// about one npm-installed coding-agent CLI. Collected by
/// <see cref="LocalCliInstallInspector"/>, consumed by the pure
/// <see cref="LocalCliInstallDiagnosis"/>.
/// </summary>
public sealed record LocalCliInstallFacts
{
    /// <summary>CLI type key, e.g. <c>claude</c> or <c>codex</c>.</summary>
    public required string CliType { get; init; }

    /// <summary>npm package that owns the binary, e.g. <c>@anthropic-ai/claude-code</c>.</summary>
    public required string PackageId { get; init; }

    /// <summary>The CLI answered <c>--version</c> with exit code 0.</summary>
    public bool VersionProbeOk { get; init; }

    /// <summary>First line of the <c>--version</c> output; null when the probe failed.</summary>
    public string? ProbedVersion { get; init; }

    /// <summary>
    /// The npm global bin directory was located, so shim and package
    /// observations below are meaningful. False on a host without a global
    /// npm install (a native-installer-only or container host), where the
    /// absence of a shim proves nothing.
    /// </summary>
    public bool NpmGlobalBinResolved { get; init; }

    /// <summary>A launchable bin shim for this CLI exists in the npm global bin directory.</summary>
    public bool ShimPresent { get; init; }

    /// <summary>
    /// Dot-prefixed atomic-rename leftovers (<c>.claude.cmd-A8DH7lDq</c>) are
    /// present. That is the documented half-installed-shim class owned by
    /// <c>tools/check-cli-shims.sh</c>, not by this feature.
    /// </summary>
    public bool OrphanShimsPresent { get; init; }

    /// <summary>The package directory still exists under the npm global <c>node_modules</c>.</summary>
    public bool PackagePresent { get; init; }

    /// <summary><c>version</c> read from the package's <c>package.json</c>; null when unreadable.</summary>
    public string? PackageVersion { get; init; }
}

/// <summary>
/// The four shapes a local CLI install can be in, ordered by how much the
/// operator has to care.
/// </summary>
public enum LocalCliInstallState
{
    /// <summary>The CLI answers <c>--version</c>. Nothing to do.</summary>
    Ready,

    /// <summary>
    /// The npm package is installed but its bin shims are gone. Observed twice
    /// on the Windows control plane (2026-08-13, 2026-08-18), both times with
    /// the CLI's own auto-update as the suspected trigger. Repairable without
    /// operator input because the operator's intent to have this CLI is still
    /// on disk.
    /// </summary>
    ShimMissingPackagePresent,

    /// <summary>
    /// Package present, shims present or half-renamed, but the binary does not
    /// run: the half-installed-stub / atomic-rename class already owned by
    /// <c>tools/check-cli-shims.sh</c> and <c>NpmShimHealer</c>.
    /// </summary>
    PackageBroken,

    /// <summary>No package, no shim. The CLI was never installed or was removed on purpose.</summary>
    NotInstalled,

    /// <summary>The CLI does not run and the host exposes no npm global bin to inspect.</summary>
    Unknown,
}

/// <summary>What, if anything, this feature is allowed to do about a state.</summary>
public enum LocalCliRepairAction
{
    /// <summary>Healthy: no action.</summary>
    None,

    /// <summary>
    /// Deferred to the existing shim repair (<c>tools/check-cli-shims.sh</c> at
    /// boot, <c>NpmShimHealer</c> pre-spawn). This feature diagnoses and
    /// surfaces the state but does not duplicate that repair.
    /// </summary>
    RestoreShims,

    /// <summary>Re-run <c>npm install -g &lt;package&gt;</c>: the only side effect this feature owns.</summary>
    GlobalReinstall,

    /// <summary>Nothing safe to do automatically. Surface it and let the operator decide.</summary>
    EscalateToOperator,
}

/// <summary>Verdict for one CLI: a state, the action it licenses, and one line of operator-facing prose.</summary>
public sealed record LocalCliInstallDiagnosisResult(
    LocalCliInstallState State,
    LocalCliRepairAction Action,
    string Summary);

/// <summary>
/// Pure classification of <see cref="LocalCliInstallFacts"/>. No IO, no clock,
/// no configuration: the whole branching lifecycle of "is this CLI broken, and
/// may we fix it" is one total function over observed facts, covered by a
/// direct matrix test.
///
/// <para>
/// The distinction the card asks for lives in rules 3 and 5 below:
/// <b>missing shim with the package still present</b> is a repairable
/// regression of an install the operator asked for, while <b>truly
/// uninstalled</b> must never trigger an automatic global npm install.
/// </para>
/// </summary>
public static class LocalCliInstallDiagnosis
{
    public static LocalCliInstallDiagnosisResult Diagnose(LocalCliInstallFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // 1. The probe is the ground truth. It resolves through PATH, so a
        //    working native-installer binary counts as ready even when the npm
        //    install alongside it is in pieces.
        if (facts.VersionProbeOk)
        {
            return new(LocalCliInstallState.Ready, LocalCliRepairAction.None,
                $"{facts.CliType} answers --version ({facts.ProbedVersion ?? "version unreported"}).");
        }

        // 2. Without an npm global bin there is nothing to compare against:
        //    a missing shim is not evidence of anything on this host.
        if (!facts.NpmGlobalBinResolved)
        {
            return new(LocalCliInstallState.Unknown, LocalCliRepairAction.EscalateToOperator,
                $"{facts.CliType} does not answer --version and no npm global bin directory was found on this host.");
        }

        // 3. Never auto-install a CLI the operator never installed. Absent
        //    package plus absent shim is a deliberate state, not a defect.
        if (!facts.PackagePresent)
        {
            return new(LocalCliInstallState.NotInstalled, LocalCliRepairAction.EscalateToOperator,
                $"{facts.CliType} is not installed: no {facts.PackageId} package under the npm global node_modules.");
        }

        // 4. Atomic-rename leftovers are the pre-existing half-installed-shim
        //    class. Renaming them back needs no network and is already done by
        //    tools/check-cli-shims.sh at boot and NpmShimHealer pre-spawn, so
        //    this feature reports rather than repairs.
        if (facts.OrphanShimsPresent)
        {
            return new(LocalCliInstallState.PackageBroken, LocalCliRepairAction.RestoreShims,
                $"{facts.CliType} has orphaned npm shims (interrupted atomic rename); tools/check-cli-shims.sh owns this repair.");
        }

        // 5. The AGT-2673 shape: package on disk, bin shims gone, no rename
        //    leftovers to restore. A global reinstall puts the shims back and
        //    is what the operator did by hand on both occurrences.
        if (!facts.ShimPresent)
        {
            return new(LocalCliInstallState.ShimMissingPackagePresent, LocalCliRepairAction.GlobalReinstall,
                $"{facts.CliType} bin shims are missing while {facts.PackageId}"
                + $"{FormatVersion(facts.PackageVersion)} is still installed; a global reinstall restores them.");
        }

        // 6. Shim present, package present, binary still refuses to run: the
        //    half-installed-stub shape. Same owner as rule 4.
        return new(LocalCliInstallState.PackageBroken, LocalCliRepairAction.RestoreShims,
            $"{facts.CliType} has a bin shim and an installed package but does not answer --version; "
            + "this is the half-installed-stub shape owned by tools/check-cli-shims.sh.");
    }

    private static string FormatVersion(string? packageVersion)
        => string.IsNullOrWhiteSpace(packageVersion) ? "" : $"@{packageVersion}";
}
