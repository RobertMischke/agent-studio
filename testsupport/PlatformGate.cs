using Xunit;

namespace AgentStudio.TestSupport;

/// <summary>
/// Marks a test as bound to a specific operating system.
///
/// The rule this encodes: a test may only be gated when the *behaviour under
/// test* is unavailable on the current platform - Linux kernel interfaces
/// (<c>/proc</c>, cgroups, <c>setsid</c>), systemd, or Unix file modes. A test
/// that merely hard-codes a Unix path or assumes LF line endings is a portability
/// defect and must be fixed, not gated. See
/// <see cref="PosixShell"/> for the shell/path side of that.
///
/// Usage mirrors the existing <c>Category=MachineBound</c> convention: an
/// explanatory comment, the trait for filtering, and the gate itself.
///
/// <code>
/// // Linux-only: the worktree proof reads /proc/&lt;pid&gt;/cwd.
/// [SkippableFact]
/// [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
/// public void Pid_with_a_different_worktree_is_not_adopted()
/// {
///     PlatformGate.LinuxOnly("the worktree proof reads /proc/&lt;pid&gt;/cwd");
///     ...
/// }
/// </code>
///
/// Gated tests report as <b>Skipped</b> with their reason, not as passed and not
/// as filtered away. An unfiltered <c>dotnet test</c> therefore stays green on
/// Windows while still stating out loud what was not covered - which is why the
/// release pipeline needs no platform filter at all.
/// </summary>
public static class PlatformGate
{
    /// <summary>Trait key, deliberately parallel to <c>Category=MachineBound</c>.</summary>
    public const string TraitName = "Platform";

    public const string Linux = "Linux";
    public const string Windows = "Windows";

    /// <summary>
    /// Skip unless the test host is Linux. <paramref name="because"/> names the
    /// concrete Linux facility the test depends on, e.g. "reads /proc/&lt;pid&gt;/cwd".
    /// </summary>
    public static void LinuxOnly(string because)
        => Skip.IfNot(OperatingSystem.IsLinux(), $"Linux-only: {because}.");

    /// <summary>
    /// Skip unless the test host is Windows, for the mirror case (Win32 handles,
    /// drive letters, <c>cmd.exe</c> semantics).
    /// </summary>
    public static void WindowsOnly(string because)
        => Skip.IfNot(OperatingSystem.IsWindows(), $"Windows-only: {because}.");

    /// <summary>
    /// Skip when this host cannot execute a POSIX shell at all. Distinct from
    /// <see cref="LinuxOnly"/>: the behaviour is portable, the host is merely
    /// missing an interpreter, so the reason must not claim Linux-boundness.
    /// </summary>
    public static void RequiresPosixShell()
        => Skip.IfNot(
            PosixShell.IsAvailable,
            "No POSIX shell on this host (install Git for Windows or put bash on PATH).");
}
