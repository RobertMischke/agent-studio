# Develop to main promotion

`develop` is the work line. `main` is the release line. The standard promotion
train prepares one exact merge commit, runs the complete blocking gate against
that commit, publishes an annotated release marker with `main`, and hands the
new `main` SHA to the external Stable deploy watcher.

Use [the promotion command](../../scripts/release/promote-develop-to-main.sh)
for this repository. It is an operator command, not a worker pipeline step.
Managed task agents must leave commit, merge, tag, and push ownership to the
platform and operator boundary described in the
[commit and push doctrine](./git/commit-push-doctrine.md).

## Safety contract

The command fails closed when any of these facts is false:

- the operator checkout is clean and its `HEAD` equals fetched
  `origin/develop`;
- an optional required convergence commit is reachable from `develop`;
- the annotated `release/*` marker does not already exist;
- the `develop` to `main` merge has no conflict, unless the operator explicitly
  enables and records the bootstrap-only develop-preferred policy;
- every command in
  [promotion-full-gate.sh](../../scripts/release/promotion-full-gate.sh) passes
  against the exact candidate merge commit;
- the gate emits its completion marker and leaves the candidate checkout clean;
- neither remote branch moves while the gate is running;
- the server accepts one atomic push containing both `main` and the annotated
  release marker; and
- remote verification resolves both refs to the tested candidate SHA.

The command never force-pushes. A red or incomplete gate has no override. The
temporary merge commit and tag are removed with the temporary worktree after a
blocked run, while the durable evidence directory remains.

## Operator checklist

1. Pause new Agent Studio pickup in the operator UI and let active integration
   work settle. This avoids spending a full gate on a `develop` tip that is
   likely to move.
2. Update the operator checkout to the current `origin/develop`. Confirm
   `git status --short --branch` is clean.
3. Preview the graph and manifest:

   ```sh
   ./scripts/release/promote-develop-to-main.sh \
     --dry-run \
     --required-ancestor 0f5372fce
   ```

4. Review `manifest-summary.tsv`, `manifest-commits.tsv`,
   `main-only-patch-review.txt`, `candidate-whitespace-review.txt`, the
   candidate commit, and any conflict list in the reported evidence directory.
   Historical committed whitespace is review evidence rather than a release
   veto; the mandatory build and test gate remains blocking. This file-level
   manifest is the manual bridge until the in-product REL-1 surface adds live
   task acceptance groups. It does not claim that integration equals
   acceptance.
5. Run the promotion with a deliberate release marker:

   ```sh
   ./scripts/release/promote-develop-to-main.sh \
     --execute \
     --required-ancestor 0f5372fce \
     --tag "release/$(date -u +%Y%m%d-%H%M%SZ)"
   ```

6. Verify `promotion-record.json` says `status=promoted`, `gate=passed`, and
   `atomicPush=true`. Verify the remote `main` and peeled tag resolve to the
   recorded `candidateSha`.
7. Resume normal pickup. The deploy cron described below detects the new
   `main`, waits for Stable to become safe to restart, runs `update-stable.sh`,
   verifies the deployed checkout, and resumes the runner.

The 1 August pre-promotion convergence at `0d8d6794a` merged the remaining
`main` fixes into `develop`. The first promotion after that convergence must use
the normal conflict-blocking path shown above. The bootstrap-only escape hatch
is retained for recovery rehearsals and older repository states, but is not
required for the current train. If an explicitly reviewed historic bootstrap
does need it, use:

```sh
./scripts/release/promote-develop-to-main.sh \
  --execute \
  --required-ancestor 0f5372fce \
  --prefer-develop-conflicts \
  --tag "release/$(date -u +%Y%m%d-%H%M%SZ)"
```

That option replaces every conflicted path with the exact `develop` version and
records both the policy and path list. It is not the routine release policy. A
current conflict requires a fresh review and should normally be converged on
`develop` before promotion.

## Mandatory full gate

The gate uses the same honest-CI principle as tag-bound releases, but it does
not publish versioned binaries. It runs, in order:

1. .NET restore;
2. frontend `npm ci` and the critical production dependency audit;
3. release shell contract tests, including promotion and deploy-watcher tests;
4. the .NET Release build;
5. every non-machine-bound .NET test in the solution;
6. frontend lint and type-check;
7. frontend unit tests; and
8. the production frontend build.

Machine-bound suites remain separately scheduled evidence and are never hidden
inside the normal green result. The promotion gate excludes them explicitly,
matching the repository test contract and release workflow.

## Release marker and evidence

The annotated `release/<UTC timestamp>` tag is a promotion marker. It is not a
`stable/*` freeze and does not trigger the `vX.Y.Z` asset workflow. A stable
claim still requires the separate stable-freeze evidence described in
[release semantics](../concepts/release-semantics.md) and
[the Stable release contract](./stable-release-contract.md).

By default the command writes evidence beneath Git's local
`promotion-results` path. Set `PROMOTION_EVIDENCE_DIR` or pass
`--evidence-dir` to use an operator-owned durable location. The record includes
the old and new branch SHAs, required ancestor, merge policy, conflict paths,
gate-script blob, gate result, tag, and atomic-push result. Logs include the
full gate output and remote push response.

## Deploy cron handoff

The one-shot Stable watcher now supports `ATP_RESTART_TRIGGER=main-advance`.
This is the handoff for the repository-backed operator Stable checkout. It does
not replace the immutable `vX.Y.Z` plus build-manifest deployment contract for
packaged installations. Run it from a host scheduler with an external lock so
long updates cannot overlap:

```cron
* * * * * flock -n /var/lock/agent-studio-main-deploy.lock env ATP_RESTART_TRIGGER=main-advance ATP_WORKSPACE=/srv/agent-taskboard-workspace ATP_STABLE_CHECKOUT=/srv/agent-taskboard-stable ATP_UPDATE_SCRIPT=/srv/agent-taskboard-devspace/update-stable.sh /srv/agent-taskboard-dev/scripts/supervisor/restart-stable-after-batch.sh >>/var/log/agent-studio-main-deploy.log 2>&1
```

The tick is a no-op while Stable already matches remote `main`. When `main`
moves, it still requires runner-idle state and the existing bounded merge-gate
drain before invoking the updater. If Stable remains behind after a successful
update or a transient remote check fails, the structured restart log names the
condition and a later cron tick retries. The watcher never changes task state.

## Failure and recovery

- Conflict, gate, or ref-movement failure: inspect the evidence, converge the
  branch if needed, fetch the new tips, and start a new run. Do not reuse the
  stale candidate.
- Atomic push failure: remote `main` and the release marker remain unchanged.
  Fix credentials or branch policy, then rerun from fresh refs.
- Deploy failure: the promotion remains a valid release fact. Diagnose the
  external updater and use its rollback procedure; do not rewrite `main` or
  move the release marker.
- Released regression: revert the merge through normal `develop` work and run
  a new promotion. Reserve an immutable Stable rollback for the deployment
  incident response path.
