# Workspace repository lifecycle

The Task Repository is a platform-owned Git repository. It stores task state
and review evidence, while the backend keeps runtime-only streams on the same
disk outside Git history. The lifecycle worker applies this policy on startup
and once per hour by default.

## Data classification

| Path family | Classification | Git policy |
|---|---|---|
| `projects/**` task state, reports, prompts, and run evidence | Durable state and evidence | Committed at task boundaries or in debounced evidence batches |
| Other tracked workspace files | Durable state or evidence | A sweep commits leftover tracked modifications within one hour |
| `logs/bus/**` | Rotating runtime projection | Kept on disk, ignored, and removed from tracking |
| `.metadata/attempt-authority*`, including `.bak` and daily archives | Live authority runtime state | Kept on disk, ignored, and removed from tracking |
| One file larger than 50 MiB | Rejected artifact | Never passed to `git add`; the backend logs the path, size, and limit |

Attempt authority remains the live fencing authority even though it is not a
Git artifact. `AttemptAuthorityService` compacts terminal records into daily
archives. Its terminal retention count is configured with
`AttemptAuthority:TerminalRetentionCount` and defaults to 2,000.

## Commit and push cadence

`WorkspaceEvidenceBatcher` groups normal task transitions. The repository
lifecycle worker also commits any remaining tracked modification once per
`WorkspaceRepositoryMaintenance:IntervalMinutes`, which defaults to 60. Both
paths share one repository index lock.

Every workspace commit enters one push queue. The queue has one reader and the
push worker also holds a repository-specific gate, so two pushes for the same
repository cannot overlap. Before pushing, the worker measures
`origin/<branch>..HEAD` and local object bytes. It warns at 50 commits or 512
MiB by default.

The first push uses the normal 30-second Git network cap. If that attempt times
out, later attempts use `WorkspaceArtifacts:CatchUpTimeoutSeconds`, which
defaults to 600 seconds. After the third failure, the backend emits both the
existing managed-repository bus error and a high-severity supervisor advisory.
The advisory names the repository and measured ahead count.

## Git maintenance

The same hourly worker waits for the system CPU load gate before maintenance.
It sets these local repository values:

```text
gc.auto=2000
maintenance.strategy=incremental
core.fsmonitor=true   # Windows
core.fsmonitor=false  # other operating systems
```

The Windows file-system monitor is enabled because the production repository
is on local NTFS and repeated full index scans are expensive. Other platforms
retain the conservative disabled setting. Each pass performs a local repack,
updates the commit graph and incremental multi-pack index, then prunes loose
objects already present in packs. Each command has a configurable 10-minute
bound. Network operations are not part of maintenance.

Configuration lives under `WorkspaceArtifacts`, `WorkspaceEvidence`, and
`WorkspaceRepositoryMaintenance` in `backend/appsettings.json`.

## Backlog recovery

Normal recovery is automatic: leave the backend running and watch for
`workspace-artifact-push-succeeded`. Do not repeatedly restart it because each
restart discards the current Git pack process.

For a manual recovery window, pause the backend workspace writers, open the
Task Repository, and inspect before changing anything:

```bash
git status --short
git rev-list --count origin/main..HEAD
git count-objects -v
```

Confirm that `logs/bus/` and `.metadata/attempt-authority*` are ignored:

```bash
git check-ignore -v logs/bus/<project>/<date>.jsonl
git check-ignore -v .metadata/attempt-authority.json
```

Then consolidate objects and push without a short wrapper timeout:

```bash
git repack -d -l
git maintenance run --task=commit-graph --task=incremental-repack
git prune-packed
git push origin HEAD:refs/heads/main
```

Resume the backend only after `git rev-list --count origin/main..HEAD` reports
zero. If the push still fails, use the supervisor advisory's repository, ahead
count, and Git error as the incident record. Never add an attempt-authority
file to work around the failure.
