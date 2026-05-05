# Taxonomy: orchestrator-prep lane + autonomy scale

Companion to [README.md](README.md) and [ui.html](ui.html). This file enumerates the configurable knobs, the clarity-score inputs, the per-autonomy-level transition matrix, the typed bounce reasons, and the override surface.

Stable references for code that needs to read or write these values:

- Setting key: `Orchestrator:AutonomyLevel` (per-project; `project-settings.json`).
- Folders: `1a-orchestrator-prep`, `1b-needs-human-review` (workspace root).
- Hosted service: `OrchestratorPrepHostedService` (Layer 1 augmentation; runs in the dev backend).
- Token-budget guard: `Orchestrator:PrepCallsPerHour` (default 30, off when service disabled).

## Configurable knobs

| Key                                     | Type     | Default | Where    | Notes                                                                              |
|-----------------------------------------|----------|---------|----------|------------------------------------------------------------------------------------|
| `Orchestrator:PrepEnabled`              | bool     | false   | global   | Master switch. Off ships in `appsettings.json`. Per-project enable lives below.    |
| `Orchestrator:AutonomyLevel`            | int 0..4 | 2       | project  | The slider in the project header. `0=manual`, `4=fully-auto`.                      |
| `Orchestrator:QueueFloor`               | int      | 2       | project  | Refill `2-ready` when its size drops below this. Ignored at level 0.               |
| `Orchestrator:MaxPrepIterations`        | int      | 3       | project  | Per-task iteration cap inside `1a-orchestrator-prep`. Level 1 caps at 1 regardless.|
| `Orchestrator:PrepCallsPerHour`         | int      | 30      | global   | Rate limit on the prep loop's outbound model calls.                                |
| `Orchestrator:PrepClaritySharpenAt`     | double   | 0.40    | global   | Below this score, the orchestrator iterates rather than accepting.                 |
| `Orchestrator:PrepClarityAcceptAt`      | double   | 0.70    | global   | At or above this score, the orchestrator accepts on the first pass.                |
| `Orchestrator:StuckLaneThresholdHours`  | double   | 24.0    | global   | Anything stuck longer in any prep lane raises a supervisor advisory.               |

Per-project overrides live in `project-settings.json` under the `orchestratorPrep` object. The global defaults live in `appsettings.json`.

## Clarity score inputs

The clarity score is computed once per iteration. Inputs are heuristic in the first slice; a fast-model verdict may replace them later.

| Input                                  | Weight | Direction                                                              |
|----------------------------------------|--------|------------------------------------------------------------------------|
| Has explicit "Read first" section      | +0.15  | Tasks that point at prior context are clearer.                         |
| Has acceptance criteria / "Done when"  | +0.20  | Concrete success conditions raise the score.                           |
| References a mockup or spec folder     | +0.10  | Tasks that ground themselves in design artifacts are clearer.          |
| Word count between 80 and 1500         | +0.10  | Too short is underspecified; too long is unfocused.                    |
| Names files or paths to touch          | +0.10  | Action surface anchored to disk.                                       |
| Conflicts with predecessor in queue    | -0.20  | Detected via shared file/path/concept; a follow-up that contradicts.   |
| Conflicts with successor in queue      | -0.10  | Detected the same way; lower weight because order can be reshuffled.   |
| Mentions a non-goal listed in ROADMAP  | -0.30  | Hard out-of-scope tokens (`worktree`, `branch-per-task`, etc.).        |
| Empty or trivially short prompt        | -0.40  | Catches "fix the bug" with no further context.                         |

Score is clamped to `[0, 1]`. The bands `<0.40 / 0.40-0.69 / >=0.70` map to `iterate / borderline / accept`.

## Per-level transition matrix

Rows are autonomy levels. Columns are the verdicts the orchestrator emits per task.

| Level | accept (>=0.70)                         | borderline (0.40..0.69)                    | iterate (<0.40)                            | post-iteration cap reached                 |
|-------|-----------------------------------------|--------------------------------------------|--------------------------------------------|--------------------------------------------|
| 0 manual    | stays in `1-preparation`           | stays in `1-preparation`                   | stays in `1-preparation`                   | stays in `1-preparation`                   |
| 1 cautious  | -> `2-ready` if iteration >= 1     | -> `1b-needs-human-review` (typed)         | -> `1b-needs-human-review` (typed)         | -> `1b-needs-human-review` (typed)         |
| 2 balanced  | -> `2-ready`                       | -> `2-ready`                               | iterate up to N; then `1b-needs-human-review`| -> `1b-needs-human-review` (typed)        |
| 3 confident | -> `2-ready`                       | -> `2-ready`                               | iterate up to N; then `2-ready` + advisory | -> `2-ready` + supervisor advisory         |
| 4 fully-auto| -> `2-ready`                       | -> `2-ready`                               | iterate up to N; then `2-ready` + chat-note| -> `2-ready` + `[supervisor]` chat-note     |

At autonomy 0 the only forward path is a human click in the kanban. The orchestrator may still **inspect** the task and append a chat-note suggesting a rewrite, but it does not move the folder.

## Typed bounce reasons

Bounces from `1a-orchestrator-prep` to `1b-needs-human-review` carry a structured reason. The kanban card surfaces the headline; the detail panel shows the body. Reasons are stored in `job.json` under `orchestratorPrep.bounceReason`.

| Code                | Headline                            | Body content                                                            |
|---------------------|-------------------------------------|-------------------------------------------------------------------------|
| `missing-criteria`  | No acceptance criteria              | Quote the prompt; suggest a "Done when" block; show the auto-rewrite.   |
| `conflicts-prev`    | Contradicts the previous task       | Name the predecessor; quote the conflicting line; ask which one wins.    |
| `out-of-scope`      | Touches a documented non-goal       | Name the non-goal token; cite the line in ROADMAP.md.                    |
| `under-specified`   | Too thin to act on                  | List the missing pieces; show a 1-paragraph rewrite the human can edit. |
| `iteration-cap`     | No convergence after N passes       | Show the diff between iter-1 prompt and iter-N prompt; ask for a call.   |
| `external-input`    | Needs information the orchestrator cannot infer | Name the unknown (project version, branch, secret, person). |

A bounce always writes:

- `prompt-suggested.md` next to `prompt.md` (the orchestrator's proposed rewrite).
- A `[orchestrator-prep]` chat-note in `cli-output.log`.
- A `bounceReason` field in `job.json` matching the table above.

## Iteration record

Each iteration in `1a-orchestrator-prep` writes:

| File / field                            | Content                                                            |
|-----------------------------------------|--------------------------------------------------------------------|
| `prompt-iter-{N}.md`                    | Snapshot of `prompt.md` before the iteration.                      |
| `prompt.md`                             | The sharpened prompt (replaces the previous content).              |
| `job.json` -> `orchestratorPrep.iteration` | Integer counter, starts at 0, increments per iteration.         |
| `job.json` -> `orchestratorPrep.lastVerdict` | One of `accept`, `borderline`, `iterate`, `bounce`.            |
| `job.json` -> `orchestratorPrep.lastClarity` | Double 0..1, the most recent clarity score.                    |
| `cli-output.log`                        | `[orchestrator-prep]` chat-note describing the change.             |

The card in the kanban surfaces the iteration count and the last verdict.

## Override surface

The user can override any orchestrator decision with a normal kanban move:

- A card dragged from `1a-orchestrator-prep` to `2-ready` short-circuits the prep loop. The decision is logged as `user-override: bypassed-prep`.
- A card dragged from `1b-needs-human-review` back to `1-preparation` resets the iteration counter and clears the bounce reason.
- A card dragged from `1b-needs-human-review` directly to `2-ready` is allowed; it's logged as `user-override: bypassed-bounce`.

The autonomy slider can be moved at any time. The next pickup tick honours the new value; an in-flight iteration finishes under the old value (no mid-iteration policy switch).

## Lane visibility rules

| Lane                       | Visible when                                                            |
|----------------------------|-------------------------------------------------------------------------|
| `1-preparation`            | Always.                                                                 |
| `1a-orchestrator-prep`     | Always (rendered as a thin rail when empty if autonomy >= 1; collapsed when empty if autonomy = 0). |
| `1b-needs-human-review`    | Only when at least one job lives there. Hide-when-empty rule.            |
| `2-ready`                  | Always.                                                                 |
| `3-progress`               | Always.                                                                 |
| `4-auto-review`            | Always.                                                                 |
| `5-human-review`           | Hide-when-empty (existing rule from kanban spec).                       |
| `6-completed`              | Always.                                                                 |
| `7-archive`                | Always.                                                                 |
| `failed-pickup`            | Hide-when-empty (existing rule).                                        |

## Why the autonomy scale is the load-bearing knob

The orchestrator's primary mandate is "the queue must not stop." Without the scale, every "is this task clear enough?" verdict is either always-bounce (queue stops on borderline tasks) or never-bounce (the orchestrator silently invents scope). The scale lets the user pick the trade between those failure modes per project, without code change.

## Out of scope for this slice

- The fast-model verdict on the clarity score (heuristic in the first slice).
- The "stuck task" auto-intervention upgrade (currently advisory-only).
- Cross-project autonomy linking (each project keeps its own slider).
- The auto-rewrite quality. The first slice writes a templated rewrite suggestion. A future slice may upgrade to a model-generated rewrite gated by the same token budget.
