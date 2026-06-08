# Cross-Job Statistics Service + robust/simple Meta-Caching; offloading the archive from the hot cache

Status: research / design proposal. **No production code lands in this task.**
Scope: pure design. Maps the current code, shows that the "dedicated cross-job stats service fed by a robust, persisted, incremental meta-cache" the card asks for **already exists** (the Agent Message Bus + `ITokenAggregator`), diagnoses the one real memory problem (the per-task hot cache fully hydrates ~748 archived folders), and proposes a small, focused scanner change to fix it without changing any statistic.
Card: `ASS-1649` (ARCH). Acceptance criteria are phrased as behavioral outcomes; section 7 maps each AC to a phase so the implementation can land as its own task(s) rather than one-shot here.

---

## 1. Problem (as stated)

The task scanner loads/caches **all** jobs, including ~748 archived (`7-archive`) folders, into the hot per-task cache. At ~1332 tasks the backend sits at ~650 MB RAM. Naively *not* caching the archive is rejected because the archive data is needed for **aggregated** statistics (token usage and other all-jobs roll-ups).

The card asks for:

1. a dedicated stats/job service exposing cross-all-jobs aggregates (tokens/cost/usage + more) over a clear API;
2. a **robust + simple** meta-cache: persisted aggregate cache, incrementally maintained (a new/finished job updates the aggregate), rebuilt only on invalidation, surviving restart without an expensive full rescan;
3. scanner/hot-cache: archived tasks no longer fully loaded on the expensive hot path → measurably lower backend memory;
4. statistic views (token modal / overview) return identical numbers (regression test);
5. no double truth: aggregates come from the stats service, not scattered ad-hoc scans.

The hint says: weave existing building blocks together instead of duplicating — bus-backed token aggregators, `TaskIndexCache`, `TaskScannerService`, `AgentMessageBusBridge`. Prioritise "robust and simple", no over-engineering.

## 2. Key finding: the meta-cache the card wants already exists

The decisive architectural fact is that **token/usage aggregation is already fed by a persisted store that is keyed by `(workspace, project, day)` — not by the per-task folder hot cache.** The archive's numbers do **not** live in `TaskIndexCache`; they live in the bus.

### 2.1 The bus is the persisted meta-cache

`AgentMessageBusStore` ([backend/Services/Bus/AgentMessageBusStore.cs](../../backend/Services/Bus/AgentMessageBusStore.cs)) is a file-backed, append-only store. Layout ([AgentMessageBusPaths.cs](../../backend/Services/Bus/AgentMessageBusPaths.cs)):

```
{workspace}/logs/bus/participants/<id>.json
{workspace}/logs/bus/<project>/<yyyy-mm-dd>.jsonl
{workspace}/logs/bus/_workspace/<yyyy-mm-dd>.jsonl
```

Every recorded token usage is mirrored here as a `kind:token-usage` `AgentMessage` carrying an `AgentMessageTokens` payload (`AgentMessageBusBridge.EmitTokenUsageAsync` / `EmitTokenUsageRichAsync`, [AgentMessageBusBridge.cs:429,466](../../backend/Services/Bus/AgentMessageBusBridge.cs)). Load-bearing properties:

- **Append-only + persisted.** `AppendAsync` ([AgentMessageBusStore.cs:77](../../backend/Services/Bus/AgentMessageBusStore.cs)) writes one JSONL line under a per-file semaphore; disk is the source of truth.
- **Survives restart without a full task rescan.** On first access per `(workspace, project)`, `LoadFromDisk` ([AgentMessageBusStore.cs:271](../../backend/Services/Bus/AgentMessageBusStore.cs)) replays the day-files into an in-memory projection in O(N) (`AppendInitial` + one `SortById`, [:390](../../backend/Services/Bus/AgentMessageBusStore.cs)). It reads `logs/bus/`, **not** the 1332 task folders.
- **Project/day scoped, independent of lane.** An archived task's historical token lines stay in the day-file they were written to. Archiving a task folder moves the folder; it does **not** move or invalidate the bus history. So the aggregate is complete whether or not the task is in the hot cache.

### 2.2 The incremental aggregate cache already exists

`BusAggregationCache` ([backend/Services/Bus/BusAggregationCache.cs](../../backend/Services/Bus/BusAggregationCache.cs)) is exactly the "incremental, rebuild-only-on-invalidation" cache the card describes:

- per `(workspace, project)` it holds `byModel` / `byParticipant` / `byDay` tallies + lifetime totals;
- `OnAppended` ([:82](../../backend/Services/Bus/BusAggregationCache.cs)) updates the tallies in **O(1)** on every new token message — this is the "a finished job updates the aggregate" requirement;
- the unfiltered request path (`Aggregate` with no since/until, [:98](../../backend/Services/Bus/BusAggregationCache.cs)) returns a snapshot in O(buckets) — **no message scan, no task scan**;
- `Invalidate` ([:115](../../backend/Services/Bus/BusAggregationCache.cs)) drops one project's state so the next access rebuilds — "rebuild only on invalidation";
- a `_seenIds` dedup set ([:188](../../backend/Services/Bus/BusAggregationCache.cs)) makes the backfill/append race idempotent (robustness).

The wiring: `AgentMessageBusStore.OnAppended` is the sink hook ([AgentMessageBusStore.cs:48,115](../../backend/Services/Bus/AgentMessageBusStore.cs)); it is set once at startup to drive `BusAggregationCache.OnAppended`.

### 2.3 The workspace aggregate is persisted for instant render

`TokenSummaryCacheStore` ([backend/Services/Runner/TokenSummaryCacheStore.cs](../../backend/Services/Runner/TokenSummaryCacheStore.cs)) persists the workspace-wide roll-up to `{workspace}/.runtime/token-aggregate-cache.json` with an atomic `.tmp`+rename write, tolerant to corruption (returns null + logs). `ITokenAggregator.CachedWorkspaceAggregate()` reads it so the status-bar usage modal shows a number on boot **before any poll completes**. This is the "survives restart without expensive full rescan" requirement for the headline number.

### 2.4 The clear cross-job API already exists

`ITokenAggregator` ([backend/Services/Tokens/ITokenAggregator.cs](../../backend/Services/Tokens/ITokenAggregator.cs)) is the single canonical surface; `TokenAggregationService` ([backend/Services/Tokens/TokenAggregationService.cs](../../backend/Services/Tokens/TokenAggregationService.cs)) implements it entirely over bus-backed readers. It already covers project roll-ups, lifetime summary, **`WorkspaceAggregate` (cross-all-projects)**, per-job footers, workspace timeline, heatmap, expensive-jobs, and ad-hoc one-shot usage. Consumers: `ProjectTokenUsageEndpoints`, `BusEndpoints`, `AdHocUsageEndpoints`. Phase-5 parity tests (`TokenSummaryBusParityTests`, `WorkspaceTokensTimelineBusParityTests`, `ProjectTokenUsageBusParityTests`) already pin the bus readers to the legacy folds numerically.

**Conclusion for AC1/AC2/AC5:** the dedicated stats service (`ITokenAggregator`), the robust+simple persisted incremental meta-cache (bus + `BusAggregationCache` + `TokenSummaryCacheStore`), and the single-source-of-truth contract are **already present**. The remaining work is (a) to *designate* this surface as canonical and document the contract, and (b) to make the per-task hot cache stop carrying the archive — without that surface depending on the hot cache.

## 3. The real problem: the hot cache fully hydrates the archive

`TaskScannerService.ScanAllJobsRaw` ([backend/Services/Tasks/TaskScannerService.cs:176](../../backend/Services/Tasks/TaskScannerService.cs)) enumerates **every** job folder in every lane (`7-archive` included) and parses each via `ScanJobFolder` ([:253](../../backend/Services/Tasks/TaskScannerService.cs)). `TaskIndexCache` ([backend/Services/Tasks/TaskIndexCache.cs](../../backend/Services/Tasks/TaskIndexCache.cs)) then holds the full `ImmutableList<TaskInfo>` of all ~1332 records in RAM.

The per-folder cost is not the JSON header parse; it is the **disk-walk-heavy enrichment** done for every folder, archived or not:

- `GetLastActivityTime` ([:1193](../../backend/Services/Tasks/TaskScannerService.cs)) — a recursive `Directory.GetFiles(dir, "*", SearchOption.AllDirectories)` + `GetLastWriteTime` over each task's whole `logs/` + `results/` subtree. This is the dominant cost (the comment at [:226](../../backend/Services/Tasks/TaskScannerService.cs) calls it out as the "Neuladen ist langsam" cost).
- `ResolveOutcomeIssue` ([:646](../../backend/Services/Tasks/TaskScannerService.cs)) — a 16 KB tail read + line scan of `cli-output.log`.
- `DetectCodeActivity` ([:484](../../backend/Services/Tasks/TaskScannerService.cs)) — a full `session-events.jsonl` scan when no inline commit.

For an archived task these enrichments feed UI affordances that an archived card does not need: an archive card is terminal, so its live outcome-issue chip, code-activity flag, and freshly-walked `lastActivity` are not load-bearing. Doing this work for ~748 archived folders on every cache refresh is the memory + CPU + transient-garbage cost the card targets.

## 4. Proposal: slim hydration for archived folders (AC3), bus stays the stats source (AC4)

Keep it small. Two pieces.

### 4.1 Slim-hydrate `7-archive`, do not evict it

Add a lane-aware fast path to `ScanJobFolder`: when the resolved state is `TaskStates.Archive` ([TaskModels.cs:2719](../../src/AgentTaskboard.Shared/Models/TaskModels.cs)), build the `TaskInfo` from the **cheap `task.json` header only** and skip the three expensive disk walks:

| field | hot lane | archive (slim) |
|---|---|---|
| Id, Key, Title, State, WatchPath, ProjectName, FolderPath, Agent, Kind, EpicId, Tags, CreatedAt | header | header (kept) |
| `LastActivity` | recursive subtree walk | fall back to `task.json` mtime (or `enteredLaneAt`) |
| `OutcomeIssue` | 16 KB log tail read | `null` (terminal lane) |
| `CodeActivityDetected` | session-log scan | derive from inline `commit` field only (no scan) |
| `Commits` | header | header (kept) |

**Slim ≠ absent.** The archived `TaskInfo` is still in the snapshot, so titles, slugs, and lane membership stay correct. What goes away is the recursive `GetFiles(...AllDirectories)` and the two log reads per archived folder. That is where the RAM/CPU/garbage is, not in the ~40 header fields. This keeps the change "robust und einfach": one branch in one method, no new tier, no eviction policy, no lazy-load plumbing.

> Optional second step if header-only is still too heavy at scale: make `TaskIndexCache` keep archived `TaskInfo` in a separate cold slot rebuilt on a longer TTL than the hot lanes (the hot lanes already refresh on every mutation/watcher event; the archive almost never changes). This is a *follow-on optimisation*, not required for the headline win, and is explicitly deferred to avoid over-engineering.

### 4.2 Why this does not change any statistic (AC4)

Token/usage aggregates are read from the **bus** (§2), not from `TaskInfo`. Slimming archived `TaskInfo` records removes exactly the fields the stats path never reads:

- `ITokenAggregator` / `BusAggregationCache` consume `AgentMessage.Tokens` from `logs/bus/`. They never read `TaskInfo.LastUsage`, `OutcomeIssue`, or `LastActivity`.
- `TaskInfo.LastUsage` is the *only* per-task token field, it is parsed from the `task.json` header (cheap, kept), and a repo-wide grep shows its sole `.cs` consumer is `TaskScannerService` itself — it is **not** an aggregation input. No double truth (AC5).

Regression guard for AC4: a parity test asserts `ITokenAggregator.WorkspaceAggregate(...)` and `ForProject(...)` return identical totals with the archive fully hydrated vs. slim-hydrated (the numbers must be byte-identical because they never came from those records).

## 5. The one coupling to fix before slimming

`BusBackedProjectTokenUsageReader.BuildJobsById` ([backend/Services/Tokens/BusBackedProjectTokenUsageReader.cs:120-135](../../backend/Services/Tokens/BusBackedProjectTokenUsageReader.cs)) calls `_scanner.ScanAllJobs()` to build a `jobId → TaskInfo` map. The token **numbers** come from the bus; this map only supplies the **title** for the "expensive jobs" list and the per-job detail drill-down.

Implication: the map needs archived jobs to *exist* in the snapshot (so an archived top-spender still shows its title), but it only needs the **Title** — a header field. §4.1 keeps Id+Title+State+WatchPath in the slim record, so this consumer is unaffected. The design must call this out so the slimming change is not mistaken as safe-to-evict: **the archive records must remain enumerable with their header fields; only the disk-walk enrichments are dropped.**

(Other `ScanAllJobs()` callers — board rendering, runner pickup, audits — operate on the hot lanes; archived header fields are sufficient or irrelevant for them. No caller depends on an archived task's `LastActivity`/`OutcomeIssue` being freshly walked.)

## 6. Robustness / restart story (AC2 restated against the code)

- **Cold start, headline number:** `TokenSummaryCacheStore.Read()` → instant status-bar value, no scan.
- **Cold start, per-project detail:** first request replays that project's `logs/bus/<project>/*.jsonl` via `LoadFromDisk` (O(N), bus files only). `WarmProject` ([AgentMessageBusStore.cs:63](../../backend/Services/Bus/AgentMessageBusStore.cs)) can pre-warm at boot to keep the first `/api/tasks/grouped` fast (already used for the Runbook 100K+-line bus).
- **Steady state:** every new token message updates `BusAggregationCache` in O(1) (`OnAppended`); reads are O(buckets).
- **Failure modes:** bus emit is best-effort (a write failure logs and is swallowed, never breaks the producer); the aggregate-cache file is corruption-tolerant (returns null → recompute). These already satisfy "robust".
- **Known scaling edge (document, do not fix here):** very large bus histories pay a one-time replay on first access. If that becomes the new bottleneck after the archive is removed from the hot path, the simplest mitigation is a persisted **daily roll-up snapshot** per project (one line per `(day, model, participant)`), letting `LoadFromDisk` start from the last snapshot instead of replaying raw lines. Listed as Phase 3 (optional), not built now.

## 7. AC → phase mapping (so this lands as its own tasks, not one-shot here)

This is an ARCH/design card; the behavioral ACs are an epic. Recommended split:

- **Phase 0 — designation/docs (this doc).** Declare `ITokenAggregator` + the bus the canonical cross-job stats surface; record the "no per-task-cache dependency for numbers" contract. Satisfies AC1/AC2/AC5 at the design level.
- **Phase 1 — slim archive hydration.** Implement §4.1 (lane branch in `ScanJobFolder`) + the AC4 parity test from §4.2. The measurable-memory AC3 is verified here (capture before/after backend RSS at ~1332 tasks). This is the only phase that *must* change production code.
- **Phase 2 — guard the coupling.** A test pinning that an archived top-spender still renders its title through `BusBackedProjectTokenUsageReader` after slimming (§5).
- **Phase 3 (optional) — bus daily roll-up snapshot.** Only if cold-replay becomes the new bottleneck (§6).

## 8. Explicit non-goals (avoid over-engineering)

- **No new stats microservice / no new persisted aggregate store.** Building one would duplicate the bus and create the exact "double truth" AC5 forbids. The bus already is the persisted, incremental, restart-surviving meta-cache.
- **No eviction / LRU / lazy-load tier** in Phase 1. Header-only slimming captures the disk-walk cost, which is the actual memory/CPU driver; a tiered cache is deferred to Phase 3-style follow-on only if measurement demands it.
- **No change to how token messages are produced** — `AgentMessageBusBridge` emission stays as-is; this work only changes what the *task* hot cache hydrates and documents the read contract.

## 9. Decision asked of a human (per ARCH convention)

Accept this design deliverable and let Phase 1/2 land as their own implementation task(s) — **or** re-scope `ASS-1649` to "Phase 1 only" (slim archive hydration + AC4 parity test) if a one-PR implementation is wanted under this card. An agent cannot move lanes or one-shot the full epic without compromising the §4.2/§5 safety guarantees, so this is the operator's call.
