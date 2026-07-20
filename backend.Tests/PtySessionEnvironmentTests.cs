using System.Text.RegularExpressions;
using AgentStudio.Cli;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Regression guard for AGT-1811: <see cref="PtySession.SpawnAsync"/> must seed the
/// child environment from the backend process's full environment, not start from an
/// empty dictionary. The quota probes (Claude/Codex/Gemini) spawn their CLI through
/// this method with no <c>extraEnv</c>; before the fix the child had no
/// USERPROFILE/HOME/APPDATA, could not locate <c>~/.claude/.credentials.json</c>, and
/// booted into first-run onboarding (theme picker + "Select login method") instead of
/// returning real <c>/usage</c> quota data. Inheriting the parent environment — mirroring
/// what <c>ProcessStartInfo.Environment</c> already does for free on the real task-run
/// spawn path — is what lets the CLI find its credential store.
/// </summary>
// MachineBound 19.07.: spawnt echte PTY-Prozesse, env-Uebernahme unter Last flaky
[Trait("Category", "MachineBound")]
public class PtySessionEnvironmentTests
{
    [Fact]
    public async Task SpawnAsync_InheritsParentProcessEnvironment()
    {
        // A distinctive parent-process env var the child could only see if SpawnAsync
        // copies the parent environment through. If SpawnAsync started from an empty
        // dictionary (the pre-fix bug), the child would never see this value.
        const string marker = "AGT1811_PTY_INHERIT_SENTINEL";
        const string sentinel = "credential-store-reachable-7f3c1a";
        Environment.SetEnvironmentVariable(marker, sentinel);
        try
        {
            var (app, args) = OperatingSystem.IsWindows()
                ? ("cmd.exe", new[] { "/c", $"echo INHERIT[%{marker}%]" })
                : ("/bin/sh", new[] { "-c", $"echo INHERIT[${marker}]" });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var pty = await PtySession.SpawnAsync(app: app, args: args, ct: cts.Token);

            var match = await pty.WaitForPatternAsync(
                new Regex(Regex.Escape($"INHERIT[{sentinel}]")),
                timeoutMs: 15000,
                ct: cts.Token);

            Assert.True(match is not null,
                "PtySession child process did not inherit the parent environment; " +
                $"snapshot was:\n{pty.SnapshotStripped()}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(marker, null);
        }
    }

    [Fact]
    public async Task SpawnAsync_ExtraEnvOverridesInheritedValue()
    {
        // extraEnv must still win over an inherited value of the same name — the four
        // TERM/COLORTERM/FORCE_COLOR/NO_COLOR keys and any caller override are layered
        // on top of the inherited base, not shadowed by it.
        const string marker = "AGT1811_PTY_OVERRIDE_SENTINEL";
        Environment.SetEnvironmentVariable(marker, "inherited-value");
        try
        {
            var (app, args) = OperatingSystem.IsWindows()
                ? ("cmd.exe", new[] { "/c", $"echo OVERRIDE[%{marker}%]" })
                : ("/bin/sh", new[] { "-c", $"echo OVERRIDE[${marker}]" });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var pty = await PtySession.SpawnAsync(
                app: app,
                args: args,
                extraEnv: new Dictionary<string, string> { [marker] = "override-wins" },
                ct: cts.Token);

            var match = await pty.WaitForPatternAsync(
                new Regex(Regex.Escape("OVERRIDE[override-wins]")),
                timeoutMs: 15000,
                ct: cts.Token);

            Assert.True(match is not null,
                "extraEnv did not override the inherited environment value; " +
                $"snapshot was:\n{pty.SnapshotStripped()}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(marker, null);
        }
    }
}
