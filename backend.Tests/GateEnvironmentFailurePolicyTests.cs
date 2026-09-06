using AgentStudio.Pipeline;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2720: a verify command that died inside its own toolchain before a single
/// test was discovered judged nothing about the delivery. The matrix below is the
/// whole decision: a toolchain signature makes it a gate-environment fault only
/// while no test-discovery evidence exists, and everything a completed test run
/// prints stays a product failure.
/// </summary>
public sealed class GateEnvironmentFailurePolicyTests
{
    /// <summary>
    /// The CAC-18 transcript verbatim. Every pre-main full suite on the studio
    /// ended here for four weeks while remote review stayed green 412 times.
    /// </summary>
    private const string Cac18Stderr =
        "file:///C:/Users/studio/AppData/Local/Temp/agentstudio-review-gates/"
        + "a1b2/frontend/node_modules/vite/dist/node/chunks/config.js:1911\n"
        + "        throw new Error(`Cannot access filesystem`);\n"
        + "        ^\n"
        + "    at testCaseInsensitiveFS (file:///C:/Users/studio/AppData/Local/Temp/"
        + "agentstudio-review-gates/a1b2/frontend/node_modules/vite/dist/node/chunks/config.js:1911:42)\n";

    [Theory]
    // A toolchain that crashed inside a restored tree, before discovery.
    [InlineData(Cac18Stderr, true, true)]
    [InlineData("Error: Cannot find module '@rollup/rollup-linux-x64-gnu'\n    at Module._resolveFilename", true, true)]
    [InlineData("You installed esbuild for another platform than the one you're currently using.", true, true)]
    [InlineData("Error: node_modules/@angular/build/src/builders/application/index.js: invalid ELF header", true, true)]
    // Same evidence, but this run installed the tree itself. The gate cannot
    // blame a tree it built from the delivery's own lockfile.
    [InlineData(Cac18Stderr, false, false)]
    [InlineData("Error: Cannot find module '@rollup/rollup-linux-x64-gnu'", false, false)]
    // The toolchain started and tests ran: whatever failed is the delivery's own result.
    [InlineData(
        "RUN v1.6.0 /repo/frontend\n FAIL src/app/card.spec.ts > renders\n"
        + "Error: Cannot find module './missing-helper'\n Test Files  1 failed (1)",
        true,
        false)]
    [InlineData(
        "node_modules/vite/dist/node/chunks/config.js emitted a deprecation warning\n"
        + " Test Files  42 passed (42)",
        true,
        false)]
    // Ordinary product failures carry no toolchain signature at all. These are
    // the cases a message-only heuristic gets wrong: a broken import names the
    // repository's own sources, and a build command never reaches discovery.
    [InlineData("src/app/card.component.ts:14:7 - error TS2304: Cannot find name 'wrongIdentifier'.", true, false)]
    [InlineData(
        "src/app/card.component.ts:3:22 - error TS2307: Cannot find module './removed-helper'.",
        true,
        false)]
    [InlineData("Error: Cannot find module './removed-helper'\n    at src/main.ts:4:1", true, false)]
    [InlineData("Failed! - Failed: 3, Passed: 200", true, false)]
    [InlineData("", true, false)]
    public void IsRestoredToolchainFault_SeparatesHostFaultsFromProductFailures(
        string evidence,
        bool usedRestoredDependencies,
        bool expected)
        => Assert.Equal(
            expected,
            GateEnvironmentFailurePolicy.IsRestoredToolchainFault(evidence, usedRestoredDependencies));

    [Fact]
    public void ClassifyFailure_ViteCrashInARestoredTree_IsGateEnvironmentNotCode()
    {
        var process = new BuildTestGateProcessEvidence
        {
            Phase = "verification",
            Command = "npm test",
            ExitCode = 1,
            StandardError = Cac18Stderr,
        };

        var kind = BuildTestGateRunner.ClassifyFailure(process, usedRestoredDependencies: true);

        Assert.Equal(BuildTestGateFailureKind.GateEnvironment, kind);
    }

    [Fact]
    public void ClassifyFailure_SameCrashAfterAFreshInstall_IsACodeFailure()
    {
        // This is what makes the retry terminate: the gate evicts the entry it
        // blamed, the next attempt installs from the lockfile, and a repeat
        // failure is reported as the delivery's own result instead of looping.
        var process = new BuildTestGateProcessEvidence
        {
            Phase = "verification",
            Command = "npm test",
            ExitCode = 1,
            StandardError = Cac18Stderr,
        };

        var kind = BuildTestGateRunner.ClassifyFailure(process, usedRestoredDependencies: false);

        Assert.Equal(BuildTestGateFailureKind.Code, kind);
    }

    [Fact]
    public void WithCacheDecision_NamesEveryScopeSoAHitOnABrokenTreeIsVisible()
    {
        // The CAC-18 transcript said "hit reason=lock-unchanged" on a truncated
        // tree for four weeks, but the card only ever showed a bare status. The
        // failing gate reason has to carry the decision that produced it.
        var reason = BuildTestGateRunner.WithCacheDecision(
            "`npm test` exit 1",
            [
                new BuildTestGateDependencyCacheEvidence(
                    ".", "hit", "lock-unchanged", "abc123", ["package-lock.json"], false),
                new BuildTestGateDependencyCacheEvidence(
                    "frontend", "miss", "install-incomplete", "def456", ["package-lock.json"], true),
            ]);

        Assert.Equal(
            "`npm test` exit 1; dependency-cache=.:hit(lock-unchanged),"
            + "frontend:miss(install-incomplete)",
            reason);
    }

    [Fact]
    public void WithCacheDecision_WithoutCacheEvidence_LeavesTheReasonAlone()
        => Assert.Equal(
            "`dotnet test` exit 1",
            BuildTestGateRunner.WithCacheDecision("`dotnet test` exit 1", []));

    [Fact]
    public void GateEnvironment_CountsAsInfrastructureNeverAsAProductFailure()
    {
        var result = new BuildTestGateResult(
            BuildTestGateVerdict.Fail, 1, 10, Cac18Stderr, "npm test exit 1", false, true)
        {
            FailureKind = BuildTestGateFailureKind.GateEnvironment,
        };

        Assert.True(result.IsInfrastructureFailure);
    }

    [Fact]
    public void ClassifyFailure_RedTestSuite_StaysACodeFailure()
    {
        var process = new BuildTestGateProcessEvidence
        {
            Phase = "verification",
            Command = "npm test",
            ExitCode = 1,
            StandardOutput = " Test Files  1 failed (43)\n      Tests  1 failed | 812 passed",
            StandardError = "AssertionError: expected 3 to be 4",
        };

        Assert.Equal(BuildTestGateFailureKind.Code, BuildTestGateRunner.ClassifyFailure(process));
    }

    [Fact]
    public void ClassifyFailure_BrokenBundlerReportingFileAccess_IsNotMistakenForALock()
    {
        // A vite probe that cannot read a file prints lock-shaped wording. Before
        // AGT-2720 that reached the Lock branch and burned the environmental
        // retry budget on a fault no retry could clear.
        var process = new BuildTestGateProcessEvidence
        {
            Phase = "verification",
            Command = "npm test",
            ExitCode = 1,
            StandardError =
                "Error: cannot access the file\n"
                + "    at testCaseInsensitiveFS (/repo/node_modules/vite/dist/node/chunks/config.js:1911:42)",
        };

        Assert.Equal(
            BuildTestGateFailureKind.GateEnvironment,
            BuildTestGateRunner.ClassifyFailure(process, usedRestoredDependencies: true));
    }
}
