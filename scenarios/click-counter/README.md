# Click Counter scenario

A four-task chain that builds a tiny static "click counter" web page end to end. Designed to exercise the auto-pickup runner against a project that lives **outside** both checkouts in the devspace, with a known starting state and a one-command reset. Not part of the regular test suite. Run it when you want to see the runner chain real work without burning quota on the agent-taskboard repo itself.

## What the scenario delivers

After the chain finishes, the watched workspace at `agent-taskboard-devspace/scenario-click-counter/workspace/` (a sibling of `agent-taskboard-dev` and `agent-taskboard-stable`, materialized by the reset script) contains:

- `index.html`, `style.css`, `script.js`, `README.md` written by the agent.
- A working click-counter page; open `index.html` in any browser, click `+1`, watch the counter increment.

The four tasks are:

| Order | Id | What it does |
|------:|----|--------------|
| 10 | `01-scaffold-files` | Creates the four files with empty / placeholder content. |
| 20 | `02-page-content` | Adds the visible HTML inside `<main>`. |
| 30 | `03-counter-logic` | Wires the click handler in `script.js` (constraint: do not touch `index.html`). |
| 40 | `04-verify` | Reads the artefacts, blocks if anything is missing, otherwise documents how to run and emits `[[TASK_DONE]]`. |

All four are configured to use `claude` with `claude-haiku-4-5`. Expected total cost: roughly 6-10K input + 2-4K output tokens, on the order of 5-15 cents on Haiku.

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
4. Open the board in the UI; the new "Click Counter" project shows up with four jobs in the `2-ready` lane.
5. Switch the project's runner to **auto-continuous**. The four tasks pick up in order; total wall-clock time is a few minutes on Haiku.

## Resetting between runs

Re-run `./scenarios/click-counter/reset.sh` any time. It wipes the workspace folder (artefacts, logs, session events, all state lanes) and re-copies pristine job templates back into `2-ready/`. Idempotent; safe between runs.

## Cost expectations

Per-task budget: around 1-3K tokens in, a few hundred out. Haiku pricing puts the full chain at 5-15 cents per run depending on how chatty the agent is. The runner records `lastUsage` in `job.json` after each run; check the board's per-card chips to see actual numbers.

## What this exercises

- Auto-pickup against a project the agent has never seen before.
- Sequential chain: each task assumes the previous one ran.
- A constraint clause (task 3: "do not modify `index.html`").
- A blocking check (task 4: emits `[[TASK_BLOCKED:...]]` if the previous tasks did not deliver).
- The output-contract sentinels (every task ends with one).
- A workspace that is **not** a git repo. The agent's `git status` calls in this workspace will produce harmless "not a git repository" output; the run itself still completes.

## What it does not exercise

- Continue / Steer / Extend / NewTask follow-ups (those are for an interactive session, not auto-pickup).
- The session-recovery path (the chain is fresh-start only).
- Multi-project parallelism (one project, four sequential tasks).

If you want to test those flows, run them ad hoc against this workspace once the chain is done; the workspace is yours to poke at.
