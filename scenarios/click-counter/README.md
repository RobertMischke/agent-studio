# Click Counter scenario

A six-task chain that builds a tiny static "click counter" web page end to end **and** exercises the trickier interactive paths: steering on an open spec, mid-run recovery, and a screenshot evidence drop into `results/`. Lives **outside** both checkouts in the devspace, with a known starting state and a one-command reset. Not part of the regular test suite; run it when you want to see the runner do real work without burning quota on the agent-taskboard repo itself.

## What the scenario delivers

After the chain finishes, the watched workspace at `agent-taskboard-devspace/scenario-click-counter/workspace/` (a sibling of `agent-taskboard-dev` and `agent-taskboard-stable`, materialized by the reset script) contains:

- `index.html`, `style.css`, `script.js`, `README.md` written by the agent.
- A working click-counter page; open `index.html` in any browser, click `+1`, watch the counter increment.
- A screenshot or written visual snapshot under task 05's `results/` directory.
- A "Test report" section appended to `README.md` by the verify task, summarizing files, evidence, recovery events, and per-task token usage.

The six tasks:

| Order | Id | What it does |
|------:|----|--------------|
| 10 | `01-scaffold-files` | Creates the four files with empty / placeholder content. |
| 20 | `02-page-content` | Adds the visible HTML inside `<main>`. Constraint: only `index.html`. |
| 30 | `03-counter-logic` | Wires the click handler in `script.js`. Constraint: only `script.js`. |
| 40 | `04-styling` | **Intentionally open spec** ("looks clean and intentional"). Steering checkpoint - see below. |
| 50 | `05-screenshot-evidence` | Saves `results/click-counter.png` if a headless browser is available, else `results/visual-check.md` as a written snapshot. |
| 60 | `06-verify` | Walks the workspace, reads `lastUsage` from each task's `job.json`, counts recovery events across the chain, appends a **Test report** to `README.md`, emits `[[TASK_DONE]]` or `[[TASK_BLOCKED:<reason>]]`. |

All six are pinned to `claude` with `claude-haiku-4-5`. Expected total cost: roughly 10-20K input + 3-5K output tokens, on the order of 10-20 cents on Haiku.

## One-time setup

1. Run the reset script. It scaffolds the workspace and queues the jobs:
   ```sh
   ./scenarios/click-counter/reset.sh
   ```
2. Add a watch-path entry to your local `backend/appsettings.Local.json` (the script prints the exact line at the end of its output). Roughly:
   ```json
   {
     "WatchPaths": [
       /* existing entries */,
       { "Name": "Click Counter", "RootPath": "C:\\Projects\\agent-taskboard-devspace\\scenario-click-counter\\workspace" }
     ]
   }
   ```
3. Restart the API: `./api.sh restart`.
4. Open the board in the UI; the new "Click Counter" project shows up with six jobs in the `2-ready` lane.
5. Switch the project's runner to **auto-continuous**. The six tasks pick up in order; total wall-clock time is a few minutes on Haiku.

## Manual checkpoints during the run

The chain is designed so three known-fragile flows have a deliberate trigger point. None are required for a green run; skipping them produces a happy-path verify report. Exercising them is the point of this scenario.

### A) Steering checkpoint (during / after task 04)

Task `04-styling` has an intentionally ambiguous spec. The agent will pick a styling direction; if you do not like it, send a **Steer**-mode follow-up after the task lands in `4-review`. Example:

> Tighten: dark Catppuccin palette, button radius 6px, max-width 480px centered.

Switch the chat-compose mode pill to **Steer**, paste the line, send. Verify in the chat that the orchestrator labels the run with `mode: steer` (not `mode: continue`) in the `[fallback]` / event log, and that `style.css` ends up matching your direction.

### B) Recovery rehearsal (any time during the chain)

To force the recovery path, kill the active CLI process while a task is running. Easiest:

```sh
./api.sh restart
```

This terminates any running CLI subprocess and brings the API back up. The runner pauses; flip it back to **auto-continuous**. The next pickup routes through Recovery (no captured session for the in-flight job), the orchestrator posts a compact `[fallback]` line, and the agent rebuilds context from the job folder. The `06-verify` task's "Test report" reports the recovery event count from `session-events.jsonl` files, so you have a written confirmation that the path was exercised.

For a stricter test, do this twice in the same run.

### C) Screenshot inspection (after task 05)

Open `agent-taskboard-devspace/scenario-click-counter/workspace/.orchestrator/jobs/<state>/05-screenshot-evidence/results/` in your file browser. There must be at least one of `click-counter.png` or `visual-check.md`. Confirm the file is non-empty. Task 06 will block if it isn't.

## Resetting between runs

Re-run `./scenarios/click-counter/reset.sh` any time. It wipes the workspace folder (artefacts, logs, session events, all state lanes) and re-copies pristine job templates back into `2-ready/`. Idempotent; safe between runs.

## Cost expectations

Per-task budget: around 1-3K tokens in, a few hundred out. Haiku pricing puts the full chain at 10-20 cents per run depending on how chatty the agent is and whether the screenshot path actually launches a browser. The runner records `lastUsage` in `job.json` after each run; check the board's per-card chips, or read the **Test report** that `06-verify` appends to `README.md`.

## What this exercises

- **Auto-pickup** against a project the agent has never seen before.
- **Sequential chain**: each task assumes the previous one ran.
- **Constraint clauses** (task 02 / 03: only edit one named file).
- **Open-spec / steering target** (task 04: the styling is yours; user steers if needed).
- **Evidence in `results/`** (task 05: screenshot or written fallback, but always something).
- **Verification gate** (task 06: explicit `[[TASK_BLOCKED]]` if anything is missing, with a written test report appended to `README.md`).
- **Recovery flag, when triggered** (manual step B above; verify task counts recovery events from `session-events.jsonl` and reports the count).
- **Output-contract sentinels** (every task ends with one).

## What it does not exercise

- Multi-project parallelism (one project, sequential).
- Continue / Extend / NewTask follow-ups: only **Steer** is documented as a manual checkpoint here. The other modes are interactive on top of any review-state job; you can poke at them ad hoc against this workspace once the chain is done.
- A real git workflow: the workspace is not a git repo. The agent's `git status` calls produce harmless "not a git repository" output; the run itself still completes.
