Final verification. Read all artifacts, confirm the chain delivered, and append a written report to `README.md`. This task is the test bed's pass/fail gate.

Run the following checks in order. Do not try to fix anything you find missing - if a check fails, emit `[[TASK_BLOCKED:<one-line reason>]]` immediately and stop. Fixing is not this task's job; reporting is.

**1. Source files** in the working directory:

- `index.html` contains `<h1>Click Counter</h1>`, an intro paragraph, `<button id="increment">+1</button>`, and `<p>Current count: <span id="count">0</span></p>` inside `<main>`.
- `script.js` increments a counter and writes the value into `#count` on click.
- `style.css` has real styling (more than the placeholder `/* click-counter scenario */` comment).
- `README.md` exists with at least the original heading.

**2. Visual evidence.** Look in **task 05's** job folder for `results/`. The job folder layout is `<workspace>/.orchestrator/jobs/<state>/05-screenshot-evidence/results/`. The state may be `5-completed` or `4-review` depending on when this verify runs. At least one of these must exist and be non-empty:

- `results/click-counter.png` (preferred), or
- `results/visual-check.md` (fallback).

**3. Recovery report.** Walk the workspace's job tree (`<workspace>/.orchestrator/jobs/`) and for every `logs/session-events.jsonl` you find, count the lines whose JSON contains `"kind": "recovery"`. Sum across all jobs. Note: zero is fine; this scenario only triggers recovery when the user manually exercises it.

**4. Token usage.** For each task that produced a `lastUsage` entry in its `job.json` (`01-scaffold-files`, `02-page-content`, `03-counter-logic`, `04-styling`, `05-screenshot-evidence`), read the `lastUsage.tokens` and `lastUsage.changes` strings if present. Summarize them. If `lastUsage` is missing for a task, just note "n/a" for that task.

**5. Append to `README.md`** in the working directory (NOT the job folder) two new sections:

- `## How to run` with the line: `` Open `index.html` in any modern browser. Click the **+1** button; the counter updates in place. ``
- `## Test report` formatted as a markdown checklist:
  - `- Files: index.html / style.css / script.js / README.md present and consistent.`
  - `- Visual evidence: <name of file found under results/>.`
  - `- Recovery events triggered across the chain: <count>.`
  - `- Token usage per task: <one short line per task; "n/a" when missing>.`
  - `- Verified at: <UTC timestamp>.`

**6. Emit the contract sentinel.**

- If all four checks (1, 2, 3, 4) succeeded and the README append worked, end with `[[TASK_DONE]]`.
- If a step in 1 or 2 fails, end with `[[TASK_BLOCKED:<one-line reason>]]`.

Do not modify any source file other than `README.md`. Do not change `index.html`, `style.css`, `script.js`, or any task's `job.json`.
