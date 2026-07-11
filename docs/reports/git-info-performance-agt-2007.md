# Git Info Performance, AGT-2007

Date: 2026-07-11  
Host: Windows 10.0.26200, 16 logical processors  
Reference checkout: this Agent Studio task worktree, sharing the reference repository object and ref database

## Result

The task-detail status target is met on the reference checkout. Each number is
the median of 15 samples from `GitInfoPerfMeasurementTests`.

| Path | Before | After cold | After warm | Target |
|---|---:|---:|---:|---:|
| `tasks/git/status` | 567 ms | 346 ms | 0 ms | cold < 2 s, warm < 500 ms |

The before path reproduces the previous six serial processes: repository root,
worktree list, porcelain status, branch, staged and unstaged numstat. The cold
path caches no status or repository-root result and runs the four independent
status reads concurrently. The warm path serves the one-second task-detail
status cache and starts no git process.

The original full harness also measured the batched provenance algorithm on the
same host. It replaces two `merge-base --is-ancestor` processes per attributed
commit with four concurrent `rev-list` sets.

| Attributed commits | Before p50 | After p50 |
|---:|---:|---:|
| 1 | 1,739 ms | 435 ms |
| 5 | 1,798 ms | 391 ms |
| 10 | 4,947 ms | 289 ms |

Reproduce the reference status measurement from a repository checkout:

```powershell
$env:RUN_GIT_INFO_PERF='1'
$env:GIT_INFO_PERF_REPO=(Get-Location).Path
$env:GIT_INFO_PERF_REFERENCE_ONLY='1'
dotnet test backend.Tests/OrchestratorApi.Tests.csproj --filter "FullyQualifiedName~GitInfoPerfMeasurementTests" --logger "console;verbosity=detailed"
```

Omit `GIT_INFO_PERF_REFERENCE_ONLY` to include the seeded status and provenance
scaling scenarios.

## Design and invalidation

- `GitProcessTelemetry` measures every git spawn with command, duration and exit
  code. Request scopes log stable `git-info request=...` rollups with spawn count,
  summed git time, wall time and per-command breakdown. `AsyncLocal` preserves the
  request scope across parallel workers.
- The task-detail status result is cached by task and watch path for one second.
  Expiry is its primary invalidation rule because unstaged filesystem changes do
  not move HEAD. In-process mutations and tests can call
  `InvalidateStatusCache()` when immediate refresh is required. Cache hits remain
  inside the telemetry scope and therefore produce a zero-spawn rollup.
- Repository toplevel resolution is cached by input path for the process lifetime.
  A checkout's toplevel is immutable. Failed resolutions are not cached;
  `InvalidateToplevelCache()` exists for fixture recreation.
- HEAD-keyed history values compare their stored HEAD with a HEAD probe cached for
  two seconds. A changed HEAD invalidates the dependent value automatically;
  explicit test invalidation is available through `InvalidateHeadKeyedCaches()`.
- Project inventory has a three-second per-project TTL. Its single
  `for-each-ref` call is restricted to `refs/heads`, so operational
  `refs/backups/*` never enters the scan or response. Worktree and recent-history
  reads remain bounded.
- Fixed SHA range results are content-addressed and use a 512-entry LRU. They need
  no HEAD or fetch invalidation because the input SHAs fully determine the answer.

Fetch changes remote-tracking refs, but none of the status cache's four reads use
remote-tracking refs. Fetch-sensitive callers either read live or use their own
short TTL. A fetched object cannot change a result keyed by fixed SHAs.

## Verification

- `GitServiceRunLocationTests` proves cache invalidation and preserves worktree
  containment.
- `GitProjectInventoryTests` creates `refs/backups/task-42` and proves it is absent
  from branch inventory.
- `GitProcessTelemetryTests` proves spawn accounting, nesting and parallel scope
  flow.
- `TaskProvenanceServiceTests` proves the batched reachability result matches the
  former per-commit ancestry algorithm before and after merge.
