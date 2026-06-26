# Code-Pattern Drift Watchlist

> **Maintained by agents and humans.** Add a new rule by appending a YAML
> document below. The deterministic
> [`CodePatternDriftAnalysisService`](../../backend/Features/Drift/CodePatternDriftAnalysisService.cs)
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
# Tooltip UI uses the canonical directive. Native browser title attributes
# are slow, unstyled, cannot render structured HTML, and drift from the app's
# singleton tooltip layer. Component inputs named `title` are allowed when
# they render visible headings (`<app-dialog title="...">`), so the rule
# targets native DOM tags plus generated ` title="..."` strings in renderers.
# ---------------------------------------------------------------------------
id: tooltip-canonical-directive
title: Tooltips use [appTooltip], not native title attributes
description: >
  Tooltip behaviour in frontend/src/app must go through the canonical
  standalone [appTooltip] directive under components/tooltip. Native DOM
  title attributes and generated ` title="..."` strings reintroduce delayed,
  unstyled browser tooltips; new custom tooltip components drift from the
  singleton app tooltip layer.
filePattern: frontend/src/app/.*\.(html|ts)$
excludeFilePattern: frontend/src/app/components/tooltip/
candidateMarker: title=|\[title\]|\[attr\.title\]|appTip|titleAttr|selector:\s*['"][^'"]*tooltip|class\s+\w*Tooltip
badVariant: (?i)<(?!app-|ng-|mat-|cdk-)[a-z][\w-]*(?=[^>]*(?:\s(?:title|\[title\]|\[attr\.title\])\s*=))|` title="|titleAttr\s*=|\[appTip\]|selector:\s*['"][^'"]*tooltip|class\s+\w*Tooltip
severityIfBad: Warn
```

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

```yaml
# ---------------------------------------------------------------------------
# Token aggregation should go through ITokenAggregator. Five services rolled
# their own per-surface roll-up before the canonical aggregator existed
# (see docs/domains/tokens.md). New token-aggregation code outside the
# Tokens/ or Bus/ namespaces drifts from the single source of truth and
# produces "tokens today" numbers that disagree across surfaces.
#
# Severity is Warn now that Phase 4 (legacy-service migration to bus-backed
# shims) is complete. The legacy service files stay excluded because their
# static pure folds are intentionally reused by the bus-backed readers and
# parity tests; new consumer-facing aggregation still goes through
# ITokenAggregator.
# ---------------------------------------------------------------------------
id: token-aggregation-canonical
title: Token aggregation goes through ITokenAggregator
description: >
  Token roll-ups (per-job/per-day/per-model totals over OrchestratorLogEntry.TokenUsage
  or AgentMessageTokens) belong in AgentStudio.Tokens or
  AgentStudio.Bus. Rolling a private aggregator elsewhere drifts
  from the canonical aggregator and produces inconsistent surface numbers.
filePattern: backend/.*\.cs$
excludeFilePattern: backend\.Tests|Features[/\\]Tokens[/\\]|Features[/\\]Bus[/\\]|Features[/\\]Runner[/\\](../ProjectTokenUsageService|WorkspaceTokensTimelineService|TokenSummary|TokenSummaryCacheStore|OrchestratorLog|OrchestratorRunner|OrchestratorChat|OrchestratorSession|GlobalOrchestratorSession|StuckLoopGuard)\.cs|Features[/\\]AdHoc[/\\]|OutputParsing[/\\]Usage[/\\]CliUsageParser\.cs|Shared[/\\]Models[/\\]AgentBus\.cs
candidateMarker: \.TokenUsage\b|AgentMessageTokens\b|TokenAggregateBucket\b
goodVariant: ITokenAggregator|TokenAggregationService|BusAggregationCache
severityIfBad: Warn
```

```yaml
# ---------------------------------------------------------------------------
# Frontmatter parsing should go through FrontmatterParser. Four services
# rolled their own regex+parser pair before the helper existed; they share
# enough edge-case logic (folded scalars, quote stripping, empty handling)
# that drift between them is a real risk.
# ---------------------------------------------------------------------------
id: frontmatter-canonical-helper
title: YAML frontmatter parsing uses FrontmatterParser
description: >
  Markdown files with `---` frontmatter at the top should be parsed via
  AgentStudio.Cli.FrontmatterParser
  (backend/Features/Cli/OutputParsing/FrontmatterParser.cs). Rolling a private
  regex risks drift on edge cases (folded scalars, quote stripping,
  CRLF handling).
filePattern: \.cs$
excludeFilePattern: backend\.Tests|OutputParsing[/\\]FrontmatterParser\.cs|CodePatternRuleLoader\.cs
candidateMarker: \\A---\\s\*\\r\?\\n
goodVariant: FrontmatterParser\b
severityIfBad: Warn
```

```yaml
# ---------------------------------------------------------------------------
# Orchestrator resume + rejection-recovery. Direct `ResumeAsync` calls on
# OrchestratorRunner skip the rejection-recovery fallback and re-introduce
# the 2026-05-11 bug where a stale Anthropic session id wedged the global
# orchestrator chat with "No conversation found with session ID: ..." until
# backend restart. Callers must go through `ResumeWithFallbackAsync` so the
# clear-session + one-shot fallback is impossible to skip.
# ---------------------------------------------------------------------------
id: orchestrator-resume-with-fallback
title: Orchestrator callers resume via ResumeWithFallbackAsync, not ResumeAsync
description: >
  Direct OrchestratorRunner.ResumeAsync calls must handle the
  "session rejected by Anthropic" recovery themselves. ResumeWithFallbackAsync
  encapsulates the recovery in one place; missing it means the caller will
  loop forever on a stale session id until the backend restarts.
filePattern: \.cs$
excludeFilePattern: backend\.Tests|Features[/\\]Runner[/\\]OrchestratorRunner\.cs
candidateMarker: (_runner|_orchestratorRunner)\.Resume(WithFallback)?Async\s*\(
badVariant: (_runner|_orchestratorRunner)\.ResumeAsync\s*\(
goodVariant: (_runner|_orchestratorRunner)\.ResumeWithFallbackAsync\s*\(
severityIfBad: High
```

```yaml
# ---------------------------------------------------------------------------
# Sandbox / OS-permission blockers detected by the canonical
# AgentEnvironmentDetector. On 2026-05-11 a Codex run on the Lotta dashboard
# project hit "windows sandbox: runner error: CreateProcessAsUserW failed:
# 1312" on every shell call and burned nine seconds retrying before giving
# up with no terminal sentinel, leaving the job as a generic
# "missing-terminal-sentinel" in 4-auto-review. The canonical recogniser
# now lives in AgentEnvironmentDetector + the CliExecutionServiceBase hook;
# any other site that scans CLI output for these needles drifts from the
# single source of truth and re-introduces the silent-fail path.
# ---------------------------------------------------------------------------
id: sandbox-blocker-detector
title: Sandbox / OS-permission error detection goes through AgentEnvironmentDetector
description: >
  Recognition of OS-level / sandbox-level blockers (Codex Windows sandbox,
  CreateProcessAsUserW 1312, EACCES/EPERM/Access-is-denied, claude tool
  permission denial, codex sandbox_permissions) must go through
  AgentStudio.Cli.AgentEnvironmentDetector
  (backend/Features/Cli/OutputParsing/AgentEnvironmentDetector.cs). Rolling a private
  substring check elsewhere re-introduces the 2026-05-11 failure mode where
  a host-level error read as a generic "missing-terminal-sentinel".
filePattern: backend/.*\.cs$
excludeFilePattern: backend\.Tests|OutputParsing[/\\]AgentEnvironmentDetector\.cs|Shared[/\\]Runner[/\\]AgentOutcomeAnalyzer\.cs|Execution[/\\]CliExecutionServiceBase\.cs|Features[/\\]Tasks[/\\]TaskScannerService\.cs
candidateMarker: windows sandbox: runner error|CreateProcessAsUserW failed: 1312|sandbox_permissions|Permission denied and could not request permission from user|EACCES\b|EPERM\b|Access is denied
goodVariant: AgentEnvironmentDetector\b
severityIfBad: High
```

```yaml
# ---------------------------------------------------------------------------
# `cliType` and `agent` in job.json must stay in sync. On 2026-05-12 a mass
# flip of 62 jobs from Claude to Codex via PUT /api/tasks/{id}/cli-type set
# `cliType=codex` but left `agent=claude`. The kanban card reads the icon
# from `cliType` and the text label from `agent`, so the cards rendered the
# Codex icon next to a "claude" label until the file was hand-edited. Only
# the canonical writer in TaskMutationService.SetJobCliType may touch
# `cliType` on its own; everywhere else that calls UpdateField with
# `"cliType"` must also update `"agent"` in the same call site.
# ---------------------------------------------------------------------------
id: cli-type-and-agent-must-sync
title: UpdateField("cliType", ...) must be accompanied by UpdateField("agent", ...)
description: >
  Whenever code writes the `cliType` field of a job.json, it must also write
  the matching `agent` field in the same method. The two fields address the
  same logical concept (the supported CLIs map 1:1 to agent labels); a write
  to only one of them drifts the kanban card's icon away from its text label.
  The canonical writer is TaskMutationService.SetJobCliType.
filePattern: backend/.*\.cs$
excludeFilePattern: backend\.Tests|Features[/\\]Tasks[/\\]TaskMutationService\.cs|Features[/\\]Tasks[/\\]TaskJsonFile\.cs
candidateMarker: UpdateField\s*\([^,]+,\s*"cliType"
goodVariant: UpdateField\s*\([^,]+,\s*"agent"
severityIfBad: High
```

```yaml
# ---------------------------------------------------------------------------
# Lane writes to 3-progress are reserved for the runner. On 2026-05-11 the
# auto-review verdict path routed reissues straight to 3-progress while the
# runner-pickup tick observed an empty lane mid-verdict and grabbed the next
# queued job - two jobs in 3-progress at once, violating the "one running
# job per project" invariant (ADR-0001). Every reissue path now parks the
# task in 2-ready at order 0 instead. The only legitimate writer of
# 3-progress is the pickup loop in ProjectRunner.TickAsync; everywhere else
# must route through 2-ready (with order 0 for priority) so the runner
# stays the single owner of "what is currently running".
# ---------------------------------------------------------------------------
id: lane-write-3-progress-forbidden
title: MoveJob to TaskStates.Progress is reserved for the runner pickup path
description: >
  Only ProjectRunner.TickAsync (the pickup loop) may move a job into
  3-progress. Auto-review reissues, supervisor interventions, and meta-cycle
  follow-ups must park their target in 2-ready (order 0 for priority) so
  the runner is the sole writer of the active lane; otherwise a reissue
  fired during a pickup gap can leave two jobs in 3-progress and silently
  park whichever the runner had just started.
filePattern: backend/.*\.cs$
excludeFilePattern: backend\.Tests|\.claude[/\\]|Features[/\\]Runner[/\\]ProjectRunner\.cs|Features[/\\]Tasks[/\\]TaskStateMachine\.cs|Features[/\\]Tasks[/\\]TaskTransitionService\.cs
candidateMarker: \.MoveJob\s*\(
badVariant: \.MoveJob\s*\([^,]+,\s*(?:TaskStates\.Progress\b|"3-progress")
severityIfBad: High
```

```yaml
# ---------------------------------------------------------------------------
# A task's commit set is rendered from job.commits, never from repo HEAD.
# Bug (reported 2026-06-01): cards in 4-auto-review / 5-human-review showed
# "main: 20 files" and a commit count sourced from the shared project's
# working-tree / branch HEAD (GitSummaryService) instead of the task's own
# attributed commits[]. Because the project git summary is shared across every
# card, a task frozen in a review lane advertised whatever branch state another
# job had just produced. The single source of truth for per-task commit/file
# attribution is job.commits (the attributed chain persisted by
# TaskMutationService.SetCommitAttributionOnFolder); on the frontend that is
# read through commitChainOf(job) / buildCommitChainView. GitSummaryService
# reflects live repo HEAD and is only legitimate for the 3-progress working-tree
# pill, which the card gates behind the LANES_WITH_GIT (3-progress-only) set.
# Any board render surface that pulls in GitSummaryService / gitSummary without
# that lane guard is re-introducing the repo-HEAD leak.
# ---------------------------------------------------------------------------
id: card-commit-source-not-repo-head
title: Card commit/file display sources from job.commits, not repo HEAD (GitSummaryService)
description: >
  Per-task commit and files-changed displays on board cards must read the
  attributed job.commits chain (commitChainOf / buildCommitChainView), never the
  shared project GitSummaryService, which reflects live repo HEAD / the working
  tree. The only legitimate GitSummaryService use is the 3-progress working-tree
  pill, gated behind the LANES_WITH_GIT set. A board component that references
  GitSummaryService / gitSummary without that lane guard leaks repo HEAD into
  review lanes (the "main: 20 files" regression of 2026-06-01).
filePattern: frontend/src/app/features/board/.*\.ts$
excludeFilePattern: \.spec\.ts$
candidateMarker: GitSummaryService\b|\bgitSummary\b
goodVariant: LANES_WITH_GIT\b
severityIfBad: High
```

```yaml
# ---------------------------------------------------------------------------
# Orphan detection must check whether the same slug already lives in a later
# lane before marking a 3-progress folder as orphan. On 2026-05-12 a stable
# restart that interrupted Lane-Moves left 6 phantom folders in
# 3a-failed-pickup whose real twins were already in 5-human-review: the boot
# sweep saw a 3-progress folder with no job.json, decided "orphan", and minted
# <slug>-debris-<date> / <slug>-orphan-<date> markers for jobs that had
# already completed. Every code path that flags a 3-progress folder as orphan
# (StaleProgressArchiver's debris path; CrashRecoveryService's orphan-change
# attribution) must first run a cross-lane lookup against
# 4-auto-review / 5-human-review / 6-completed / 7-archive. If the slug
# already exists downstream, the source folder is a mid-move casualty and
# must be silently reconciled away, not dead-lettered.
# ---------------------------------------------------------------------------
id: orphan-detection-checks-other-lanes
title: Orphan detection must run a cross-lane lookup before flagging
description: >
  Code that decides a 3-progress folder is an orphan (debris archive, failed
  pickup, orphan-change attribution) must first check whether the same slug
  already lives in a later lane (4-auto-review / 5-human-review / 6-completed /
  7-archive). Without that check, a Lane-Move that completed seconds before a
  backend restart leaves a residue in 3-progress that the boot sweep mints
  into a phantom card. The canonical helpers are
  `StaleProgressArchiver.TryFindSlugInLaterLane` and
  `CrashRecoveryService.SlugExistsInDownstreamLane`.
filePattern: backend/Features/Runner/(StaleProgressArchiver|CrashRecoveryService)\.cs$
candidateMarker: \b(?:ArchiveAsDebris|ArchiveDebrisFolder|MoveOrphanToFailedPickup|FindMostRecentlyActiveProgressJob)\b
goodVariant: SlugExistsIn(?:Lane|DownstreamLane)|TryFindSlugInLaterLane|DownstreamLanesForOrphanReconciliation
severityIfBad: High
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
