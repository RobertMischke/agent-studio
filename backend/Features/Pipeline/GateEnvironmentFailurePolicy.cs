namespace AgentStudio.Pipeline;

/// <summary>
/// Pure policy that separates a gate-host fault from a product failure.
/// <para>
/// CAC-18 spent 412 green remote reviews against a broken studio: a truncated
/// <c>node_modules</c> restored from the shared cache made vite abort in its
/// case-insensitive filesystem probe, the pre-main full suite exited 1 before
/// vitest listed a file, and the card was recorded as a failed delivery.
/// </para>
/// <para>
/// The hard part is saying that without ever excusing a genuinely red delivery.
/// A message alone cannot do it: <c>Cannot find module</c> is what a broken
/// import prints, and a build command never reaches test discovery, so
/// "no tests ran" is not evidence of anything on its own. The decisive fact is
/// ownership of the dependency tree. This gate only calls a toolchain crash
/// environmental when the tree it crashed in was <em>restored from the shared
/// cache</em> rather than installed by this run. A freshly installed tree is
/// the delivery's own lockfile faithfully applied, so a crash inside it is the
/// delivery's result.
/// </para>
/// <para>
/// That also makes the retry terminate. The gate evicts the entry it blamed, so
/// the next attempt installs from the lockfile; if it fails again the tree is no
/// longer cache-owned, the failure classifies as product code, and the card
/// reaches an operator through the ordinary path instead of looping.
/// </para>
/// </summary>
public static class GateEnvironmentFailurePolicy
{
    /// <summary>
    /// Evidence that the fault is inside installed tooling rather than in the
    /// repository's own sources. Bundler and runner package paths are the direct
    /// signal; the platform and native-binding messages describe a tree that was
    /// materialized for a different machine, which is the other way a restored
    /// cache entry goes wrong. Deliberately no bare "cannot find module": that
    /// is the single most common wording of an ordinary broken import.
    /// </summary>
    private static readonly string[] ToolchainFaultSignatures =
    [
        "node_modules/vite/", "node_modules\\vite\\",
        "node_modules/vitest/", "node_modules\\vitest\\",
        "node_modules/esbuild", "node_modules\\esbuild",
        "node_modules/@esbuild", "node_modules\\@esbuild",
        "node_modules/rollup", "node_modules\\rollup",
        "node_modules/@rollup", "node_modules\\@rollup",
        "node_modules/webpack", "node_modules\\webpack",
        "node_modules/typescript/", "node_modules\\typescript\\",
        "node_modules/@angular/build", "node_modules\\@angular\\build",
        "node_modules/@angular-devkit", "node_modules\\@angular-devkit",
        "node_modules/.bin/", "node_modules\\.bin\\",
        // Platform-specific binary packages. A repository's own sources never
        // name these, so an unresolvable one is always an incomplete install.
        "@rollup/rollup-",
        "@esbuild/",
        "@swc/core-",
        "testcaseinsensitivefs",
        "failed to load native binding",
        "you installed esbuild for another platform",
        "was compiled against a different node.js version",
        "invalid elf header",
        "err_dlopen_failed",
    ];

    /// <summary>
    /// Output that only exists once a runner enumerated tests. Its presence
    /// proves the toolchain started, so whatever failed afterwards is the
    /// delivery's own result. Kept to markers a runner prints verbatim: short
    /// or generic needles here silently disable the whole policy, because any
    /// accidental match vetoes the decision.
    /// </summary>
    private static readonly string[] TestDiscoverySignatures =
    [
        "test files",
        "test suites:",
        "ran all test suites",
        "starting test execution",
        "[xunit.net",
        "passed!",
        "failed!",
    ];

    /// <summary>
    /// True when a failed verify command crashed inside a dependency tree this
    /// run restored from the shared cache, before any test was discovered.
    /// </summary>
    /// <param name="evidence">Combined stdout and stderr of the failed command.</param>
    /// <param name="usedRestoredDependencies">
    /// Whether any dependency scope was served from the cache instead of being
    /// installed by this run. False makes the answer false: the gate cannot
    /// blame a tree it built itself.
    /// </param>
    public static bool IsRestoredToolchainFault(string? evidence, bool usedRestoredDependencies)
    {
        if (!usedRestoredDependencies) return false;
        var value = evidence ?? string.Empty;
        if (value.Length == 0) return false;
        return Contains(value, ToolchainFaultSignatures)
               && !Contains(value, TestDiscoverySignatures);
    }

    private static bool Contains(string value, string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
}
