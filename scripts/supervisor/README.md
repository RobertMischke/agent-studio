# Layer 3 supervisor scripts

External, read-mostly scripts that run **outside** the running stable instance. The product's runtime never starts these. They live in the dev checkout because the source travels with the rest of the repo, but every script in this folder is invoked by the user (or a host scheduler) from outside the app.

The doctrine for the layer: stable is the single state-machine authority over its own job state (ADR-0017). Anything in this folder either reads stable's state or operates on stable from outside the running process — never both at once, never in a way that lets stable mutate itself while the same `dotnet` / `ng serve` is alive.

## Scripts

### `run-system-review.sh` + `system-review.md`

A read-only review skill for the running stable instance. Drives the `system-review` skill via a CLI (`claude` by default) and writes one Markdown review file under `<workspace>/logs/system-review/<YYYY-MM-DD-HHmm>.md`. See `system-review.md` for the skill itself.

Run cadence: every 4-8 hours. Scheduling is the user's concern; this script is the entry point a cron / Task Scheduler entry can call.

### `dev-lifecycle.sh`

Narrow start/stop/status for the **dev** backend on `:5030`, used as a Playwright target by E2E specs running from stable. This is the only place outside `start-dev.sh` / `stop-dev.sh` that should touch the dev backend.

### `restart-stable-after-batch.sh` + `run-stable-restart-watcher.sh`

External orchestrator that restarts stable cleanly at quiet boundaries. The motivation is the same crash class that produced ADR-0020: stable serves the source the running tasks edit, so if stable were ever to "restart itself" mid-batch it would be replacing its own running code. ADR-0021 makes that a hard non-goal and assigns the restart responsibility to this watcher.

Trigger conditions (both must hold on a tick):

- At least N (default 3) **new** job folders have appeared in the watched project's `4-review` lane since the last restart (or since the watcher first booted and took its baseline snapshot).
- Stable's `/api/runner/status` reports every project as idle (`activeJobId == null`). If stable is unreachable, the tick is skipped — never restart blind.

On trigger the watcher delegates to `update-stable.sh` in the parent devspace folder, which already runs the preflight (clean working tree, fast-forward only), stops stable, pulls `origin/main`, runs `npm install` if `package-lock.json` changed, and re-launches stable detached. The watcher does not call `git pull` or `git fetch` directly.

Each invocation that actually restarts appends one JSON line to `<workspace>/logs/stable-restarts.jsonl`:

```json
{"ts":"2026-05-05T08:42:11Z","event":"restart","status":"ok","jobsSinceLastRestart":3,"headBefore":"2bec67c","headAfter":"a1f4b29","durationSeconds":47,"reviewCountAfter":14}
```

The watcher also persists its rolling snapshot of seen `4-review` folders at `<workspace>/logs/stable-restart-watcher/snapshot.txt`. That file plus the JSONL log are the watcher's only state — delete them to reset.

#### Running it

```sh
# foreground (Ctrl-C to stop)
./scripts/supervisor/run-stable-restart-watcher.sh

# tighter tick for an active session
ATP_RESTART_TICK_SECONDS=30 ./scripts/supervisor/run-stable-restart-watcher.sh

# different threshold or workspace
ATP_RESTART_THRESHOLD=5 ATP_WORKSPACE=/some/other/workspace \
  ./scripts/supervisor/run-stable-restart-watcher.sh
```

#### How it relates to the system-review monitor

Both watchers read stable from outside the running process; neither mutates job state. They run independently and can be started in different terminals or under different schedulers. The system review answers "is stable behaving correctly?" — the restart watcher answers "is now a safe moment to swap stable to newer source?". They share a logs root (`<workspace>/logs/`) but write to different files, so a user reviewing recent activity can tail both without crossing wires.

## Env conventions

All scripts in this folder accept the same workspace / stable-checkout overrides:

- `ATP_WORKSPACE` — workspace root (default: `C:/Projects/agent-taskboard-workspace`)
- `ATP_STABLE_CHECKOUT` — stable repo (default: sibling `agent-taskboard-stable`)

Per-script knobs are documented at the top of each script.
