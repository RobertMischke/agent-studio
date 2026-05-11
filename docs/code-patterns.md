# Code-Pattern Drift Watchlist

> **Maintained by agents and humans.** Add a new rule by appending a YAML
> document below. The deterministic
> [`CodePatternDriftAnalysisService`](../backend/Services/Drift/CodePatternDriftAnalysisService.cs)
> reads this file on every analysis pass and merges its rules with the
> hardcoded `DefaultRules`. Adding a rule does not require a rebuild.

## Why this file exists

On 2026-05-11 a Windows-prompt-via-argv regression caused every auto-review
aspect verdict to fall back to "concerns" with an empty model reply. Three
call sites had drifted from the canonical stdin-piped one-shot pattern;
five had not. The drift was invisible until a user spotted false-positive
chips on the board.

This file is the standing watchlist that prevents that class of failure.
Each rule names one canonical implementation pattern, the file glob the
detector walks, and the regexes that identify "canonical" vs "drift" sites.

## Rule grammar

Each rule is one YAML document delimited by `---`. Required keys:

| Key | Type | Meaning |
|-----|------|---------|
| `id` | string | Stable kebab-case slug. Stays the same across rule edits so historical drift reports stay linkable. |
| `title` | string | Short human-readable title (under 80 chars). |
| `description` | string | One- or two-line description of the canonical pattern + the failure mode if a site drifts. |
| `filePattern` | regex | Relative-path regex of files to consider (forward slashes). |
| `excludeFilePattern` | regex (optional) | Paths to skip (the canonical implementation, tests, build output). |
| `candidateMarker` | regex | "This file is a candidate for the rule" — cheap pre-filter. |
| `badVariant` | regex (optional) | When it matches, the site is reported as drift. |
| `goodVariant` | regex (optional) | When the candidate matches and goodVariant does not, the site is also reported as drift (evidence: `missing-canonical`). |
| `severityIfBad` | enum | `Info` / `Warn` / `High` / `Critical`. |

If only `badVariant` is set, a site is drift iff that variant matches.
If only `goodVariant` is set, a site is drift iff that variant does NOT match.
If both are set, `badVariant` wins.

## Active rules

The rules below augment `CodePatternDriftAnalysisService.DefaultRules`. The
default rules cover the three patterns that bit us in production
(cli-one-shot-stdin, jsonl-append-locked, frontend-fetch-xclientid). New
rules belong here, not in C# — adding one is a docs commit, not a backend
deploy.

```yaml
# ---------------------------------------------------------------------------
# Bus-emit fire-and-forget. EmitXxxAsync calls on AgentMessageBusBridge must
# be fire-and-forget (`_ = _bus?.EmitXxxAsync(...)`) so the bus's best-effort
# semantics do not block the canonical write path. An `await` on the bridge
# call surface from a hot path turns a transient bus IO hiccup into a stall.
# ---------------------------------------------------------------------------
id: bus-emit-fire-and-forget
title: AgentMessageBusBridge.EmitXxxAsync must be fire-and-forget
description: >
  Calls to `bridge.EmitXxxAsync(...)` should be discarded (`_ = bridge.EmitXxxAsync(...)`)
  so the bus stays observability-only. Awaiting them on a hot path turns a
  transient append failure into a blocking error for the producer.
filePattern: \.cs$
excludeFilePattern: AgentMessageBusBridge\.cs|backend\.Tests
candidateMarker: _bus\?\.Emit\w+Async\s*\(
badVariant: await\s+_bus\?\.Emit\w+Async\s*\(
severityIfBad: Warn
```

```yaml
# ---------------------------------------------------------------------------
# Process.Start without redirected stderr loses error visibility. Every
# subprocess spawn that captures stdout should also capture stderr so a
# non-zero exit can be diagnosed without re-running with strace.
# ---------------------------------------------------------------------------
id: process-start-stderr-redirected
title: Process.Start with redirected stdout also redirects stderr
description: >
  When `RedirectStandardOutput = true` is set on `ProcessStartInfo`, also
  set `RedirectStandardError = true`. Without it, child-process error
  messages disappear and "the CLI returned nothing" becomes the only signal.
filePattern: \.cs$
excludeFilePattern: backend\.Tests
candidateMarker: RedirectStandardOutput\s*=\s*true
goodVariant: RedirectStandardError\s*=\s*true
severityIfBad: Warn
```

## Adding a rule — checklist

1. **Catch a real incident.** Don't add a rule for a hypothetical drift; add
   it when you've seen a regression in production.
2. **Write the minimal regex.** Two false positives are worse than two false
   negatives — the report loses credibility fast.
3. **Add a goldenscript test.** In `backend.Tests/CodePatternDriftAnalysisServiceTests.cs`,
   write a test with a known-good and known-bad fixture file that the rule
   classifies correctly. The smoke test against the live repo catches
   regressions automatically thereafter.
4. **Land the rule before the fix.** The drift report should go from
   "1 finding, severity High" to "0 findings, severity Info" in the same
   PR as the fix. That proves the gate works for the next reviewer.
