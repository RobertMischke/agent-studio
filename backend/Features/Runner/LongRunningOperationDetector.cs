using System;

namespace AgentStudio.Runner;

/// <summary>
/// Recognises shell commands that legitimately stay stdout-silent for
/// minutes at a stretch: dev-server starts (<c>ng serve</c>), builds,
/// long test runs, and the poll loops that wait for a server/compile to
/// come up (<c>curl --retry</c>, <c>until curl</c>, <c>wait-on</c>).
///
/// <para>
/// <b>Why this exists.</b> The phase-aware watchdog measures silence
/// (no streamed CLI activity) and kills a run once silence crosses the
/// <see cref="PhaseBudget"/> for the current phase. An agent that runs
/// <c>ng serve</c> and then blocks on the Angular cold-compile produces
/// <em>no stdout</em> for that whole window even though it is doing
/// exactly the right thing - the run reads as "hung" and gets killed
/// (the ASS-665 false positive). The fix is to widen the silence budget
/// while a known long-op is the in-flight tool, so a legitimate wait on
/// a server/compile is not mistaken for a hang.
/// </para>
///
/// <para>
/// Matching is a deterministic case-insensitive substring test against a
/// curated set of command fragments. A false positive only <em>widens</em>
/// the silence budget (the kill still fires, just later, bounded by the
/// long-op budget), so the detector deliberately errs toward recognising
/// rather than missing a long-op. There is no LLM in this path (ADR-0032).
/// </para>
/// </summary>
public static class LongRunningOperationDetector
{
    // Curated command fragments, all lower-case. Ordered roughly by how
    // commonly they appear in agent runs. A command "is a long-op" when it
    // contains any of these as a substring. Tune by adding fragments here;
    // keep them specific enough that an unrelated command (e.g. echoing the
    // word "build") does not trip every run - though a stray match is only
    // a wider budget, never a missed kill.
    private static readonly string[] Patterns =
    {
        // Angular CLI - the motivating case (ASS-665 ng serve cold compile).
        "ng serve",
        "ng build",
        "ng test",
        "ng e2e",
        // Node package managers: builds, dev servers, installs.
        "npm run build",
        "npm run start",
        "npm start",
        "npm run dev",
        "npm run serve",
        "npm run watch",
        "npm run e2e",
        "npm run test",
        "npm test",
        "npm ci",
        "npm install",
        "pnpm build",
        "pnpm dev",
        "pnpm start",
        "pnpm run",
        "pnpm install",
        "pnpm test",
        "yarn build",
        "yarn dev",
        "yarn start",
        "yarn install",
        "yarn test",
        // Bundlers / dev servers.
        "vite build",
        "vite dev",
        "vite preview",
        "vite serve",
        "webpack",
        "next build",
        "next dev",
        "ng-packagr",
        // .NET.
        "dotnet build",
        "dotnet test",
        "dotnet run",
        "dotnet watch",
        "dotnet restore",
        "dotnet publish",
        "msbuild",
        // Other common build/test toolchains.
        "playwright test",
        "playwright install",
        "gradlew",
        "gradle ",
        "mvn ",
        "make ",
        "cmake",
        "cargo build",
        "cargo test",
        "cargo run",
        "go build",
        "go test",
        // Dev-server waits / poll loops: the agent is alive, polling a port.
        "wait-on",
        "wait-port",
        "curl --retry",
        "curl -s --retry",
        "curl -f --retry",
        "until curl",
        "while curl",
        "until ! curl",
        "while ! curl",
        "nc -z",
    };

    /// <summary>
    /// True when <paramref name="command"/> looks like a known
    /// long-running operation whose silence the watchdog should tolerate.
    /// </summary>
    public static bool IsLongRunningOperation(string? command)
        => TryMatch(command, out _);

    /// <summary>
    /// Like <see cref="IsLongRunningOperation"/> but also reports the matched
    /// fragment so the runner can show <em>why</em> the budget was widened in
    /// the watchdog chat note (evidence, not policy).
    /// </summary>
    public static bool TryMatch(string? command, out string matchedPattern)
    {
        matchedPattern = string.Empty;
        if (string.IsNullOrWhiteSpace(command)) return false;
        var haystack = command.ToLowerInvariant();
        foreach (var p in Patterns)
        {
            if (haystack.Contains(p, StringComparison.Ordinal))
            {
                matchedPattern = p.Trim();
                return true;
            }
        }
        return false;
    }
}
