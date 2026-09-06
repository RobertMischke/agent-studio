# Workspace Repository Lifecycle

Status: operational contract as of 2026-09-06.

The Task Repository is a Git repository of durable task and orchestration
evidence. It is also the parent directory for runtime-only state. The backend
owns commits, pushes, staging guards, and object maintenance for this
repository. Operators should not need a routine catch-up commit or push.

## Path policy

| Path family | Classification | Git behavior |
|---|---|---|
| `projects/**` task records and evidence | Durable evidence | Committed at run boundaries and in debounced transition batches. |
| `logs/bus/**` | Durable evidence | Joins transition evidence batches. An hourly sweep also captures bus writes that occurred without a transition. |
| Other already-tracked paths | Durable until deliberately migrated | The hourly sweep commits leftover working-tree changes with `chore(workspace): sweep tracked repository drift`. |
| `.metadata/attempt-authority.json` | Runtime authority | Never staged. Live terminal history is compacted by `AttemptAuthorityService`, with 2,000 terminal attempts retained by default. |
| `.metadata/attempt-authority.archive-*.json` | Runtime authority history | Never staged. Read only by explicit history queries. |
| `.metadata/attempt-authority*.bak` and temporary variants | Runtime recovery state | Never staged. Old operator-created backup files may be removed only after their recovery value has been checked. |
| Project `.orchestrator/**`, attachments, caches, temporary files, and rotated CLI logs | Runtime state | Excluded by the transition evidence policy. |

The maintenance worker ensures `.metadata/attempt-authority*` is present in
the repository-local `.git/info/exclude`. The commit service independently
enforces the same rule for already-tracked files, so an ignore rule is not the
only protection. Every add boundary also refuses an individual file above
50 MiB, logs its path, size, limit, and reason, and leaves it unstaged. The
configured size limit may be lowered but cannot be raised above 50 MiB.

## Commit and push cadence

Lane transitions are debounced for 15 seconds and forced into a batch after
60 seconds of continuous activity. Run-boundary and upload commits remain
immediate. `WorkspaceEvidence:SweepIntervalMinutes` defaults to 60 and is the
backstop for tracked paths outside those scoped commits.

Each platform-owned commit enters `WorkspaceArtifactPushQueue`. Pushes for one
repository are single-flight. The worker measures `origin/<branch>..HEAD`
before pushing and warns when either of these defaults is reached:

- 50 commits ahead (`WorkspaceArtifacts:BacklogWarningCommitCount`)
- 100 MiB estimated reachable object storage
  (`WorkspaceArtifacts:BacklogWarningBytes`)

The first push keeps the 30-second network budget. If that attempt times out,
remaining attempts use the 600-second catch-up budget so a large pack can
finish instead of restarting every 30 seconds. Three exhausted attempts emit a
high-severity `workspace-repository-push-blocked` supervisor advisory containing
the repository, target branch, and measured ahead count.

## Object maintenance

`WorkspaceRepositoryMaintenanceService` runs at backend startup and every six
hours. It waits for the host load gate before entering the same repository lock
used by commits. The hosted timer is used instead of `git maintenance start`,
so no unobserved operating-system scheduler can contend with active work.

The worker applies these local settings:

| Setting | Default | Purpose |
|---|---:|---|
| `gc.auto` | `10000` | Trigger Git's automatic housekeeping before loose objects become unbounded. |
| `maintenance.strategy` | `incremental` | Select incremental maintenance behavior. |
| `maintenance.loose-objects.batchSize` | `50000` | Consolidate a large backlog in bounded packs. |
| maintenance interval | 6 hours | Bound drift across a normal operating day. |
| maintenance command timeout | 30 minutes | Allow a large local repack to finish. |

Each pass repeats the `loose-objects` task until the loose-object count is at or
below 10,000, up to 12 passes, and then runs `incremental-repack`. On Windows,
Git 2.37 or newer enables `core.fsmonitor=true` only after the built-in daemon
starts successfully. Older versions and unsupported repositories leave it off
and produce an explicit warning.

## Manual backlog recovery

Pause task pickup first if the repository is under active write load. Run these
commands from the Task Repository, not from an application source checkout:

```bash
git status --short
git rev-list --count origin/main..HEAD
git rev-list --disk-usage origin/main..HEAD
git count-objects -v
git check-ignore -v .metadata/attempt-authority.json
```

If an attempt-authority file is staged, unstage it before any commit:

```bash
git reset -q HEAD -- '.metadata/attempt-authority*'
```

Consolidate loose objects. Repeat the first command until `count-objects -v`
reports a bounded `count`, then perform the incremental repack:

```bash
git maintenance run --task=loose-objects
git count-objects -v
git maintenance run --task=incremental-repack
```

Finally push without an external 30-second wrapper:

```bash
git push origin HEAD:refs/heads/main
git rev-list --count origin/main..HEAD
```

Do not use a force push. If the remote branch diverged, preserve both histories
and resolve the divergence as a separate operator action. A final ahead count
of zero confirms remote durability.
