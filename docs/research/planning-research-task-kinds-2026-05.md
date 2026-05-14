# Planning and Research task kinds — design exploration (2026-05)

Status: design decisions pinned (2026-05-14). No code change. Captures
user intent from the `planning-task-task-differenzierung` brainstorm,
proposes a path that reconciles it with the existing hard non-goals,
and locks in the previously-open design choices as documented defaults.
Follow-up tasks for the actual implementation are listed at the end.

## What the user asked for

Three task kinds, distinguished by what the agent is allowed to do and what
the user does with the result:

1. **Coding task** (today's default): mutates source. Subject to the existing
   "one running task per project" rule.
2. **Planning task**: read-only. Analyzes the codebase and proposes the next
   concrete piece of work. Many should be runnable in parallel. No execution
   limit (no timebox, no token cap from the orchestrator). The result is the
   point. The user wants a one-click flow to take a finished planning result
   and turn it into a new coding task: the create-task modal opens
   pre-filled, attached images carry over, the user just hits Save.
3. **Research task**: read-only. Broader than planning — finds things out,
   possibly via web search. Many parallel. No follow-up action implied; the
   deliverable is a clean report the user reads.

Planning and Research differ by *intent and tool surface*, not by execution
mechanics: planning targets "what should we build next in this codebase,"
research targets "what is true about a topic" and may want web search.

## Conflict with existing non-goals

`AGENTS.md` calls out as a hard non-goal:

> **Intra-project parallelism.** At most one task runs per project at any
> time. No fan-out across agents, machines, or branches inside one project.

That rule exists to keep the product simple and to avoid source-code
conflicts when multiple agents touch one tree. Planning and Research
explicitly do not touch source, so the *cause* the non-goal protects against
does not apply to them. But the rule as written has no carve-out, and
`ProjectRunner._activeJobId` enforces a single-slot latch independent of
task kind.

Two ways to read the request:

- **Carve-out**: keep the non-goal for mutating work, add an explicit
  exception for read-only kinds. This is consistent with the spirit of the
  rule and mostly low-risk because read-only kinds cannot collide on the
  working tree.
- **Reframe**: drop the non-goal entirely and add scheduling primitives.
  This is too big a change for what the user is asking for and erodes the
  product's main differentiator.

The carve-out is the only sensible option, and it must be written down as
an ADR before any code change so the next agent does not re-import the
old rule blindly. Suggested wording for the non-goal section:

> Coding tasks (any task that may write to the working tree) run **strictly
> serially per project**. Read-only task kinds — Planning and Research —
> may run in parallel because they cannot collide on source. The
> `one-task-per-project` invariant is a property of mutation, not of the
> queue.

## Data model implications

Today `job.json` has no `kind` field. Adding one is the minimum:

```jsonc
{
  "id": "...",
  "kind": "coding" | "planning" | "research",   // new, default "coding"
  ...
}
```

Default to `"coding"` so existing jobs and `CreateJobRequest` payloads
without the field keep working unchanged. Boot-time migration sets the
field on legacy folders; nothing else changes.

The kind is a property of the task, not of the lane. All three kinds use
the same lifecycle states. The runner branches on kind when picking up.

## Runner / scheduler implications

The hard part. `ProjectRunner._activeJobId` is a single nullable string
guarding the per-project pickup loop. It is checked on every pickup tick
and on the manual start path. Two viable shapes:

**Option A — single-slot for coding, free-for-all for read-only.**

- `_activeCodingJobId` replaces `_activeJobId` for the mutation invariant.
- Read-only kinds spin up CLI executions through a separate path that
  does not touch `_activeCodingJobId` and does not care about it.
- The pickup loop iterates `3-progress` once for coding (the existing
  strict-iteration rule still applies, ADR-0028) and once for read-only
  (no slot guard; every eligible folder starts).
- Risk: real OS process count, real disk and CPU pressure, real CLI quota
  consumed by N parallel agents. Add a soft cap (`ReadOnlyParallelism`,
  default 4? configurable per project) so a queue spike does not fork
  a hundred Claude processes.

**Option B — two slots for any kind, but coding takes the priority slot.**

Simpler invariant ("at most N concurrent CLI runs per project"), worse
match for the user's "no limit on planning" request. Reject.

Option A wins. The cap is a guardrail, not a queue: a planning job over
the cap waits in `2-ready` and starts when a slot frees, no fanciness.

Failure modes to think through before implementing:
- A read-only agent that *does* write (planning task that "helpfully"
  edits a file). Mitigation: pre-flight a `git stash --include-untracked`
  and `git stash pop` or a sandboxed checkout, or simply detect the diff
  on completion and surface it as a hard violation. Do not auto-revert;
  the user decides.
- Quota exhaustion. Read-only kinds share the same CLI quota as coding;
  if the user kicks off 8 planning runs they may starve the next coding
  task. The quota probe surface already exists; the picker should refuse
  to start a planning job when quota is below a threshold and a coding
  job is queued.
- Log volume. N parallel runs writing to the same project log directory
  is fine because each job has its own folder, but the activity-log
  aggregator and SignalR broadcasts must not assume one-active.

## "Promote planning result to a coding task"

The user's headline interaction: read the planning report, hit a button,
get the create-task modal pre-filled, hit Save.

What "pre-filled" means concretely:

- **Title**: derived from the planning task's title or its result heading
  (the planning prompt should ask the agent to suggest a one-line title
  in a fenced code block so the UI can extract it deterministically).
- **Prompt body**: the planning task's final report, possibly trimmed to
  a "task-prompt section" that the agent is instructed to write under
  a stable heading (`## Proposed task prompt`).
- **Attachments**: every image under the planning job's `results/` and
  `attachments/` folders is copied into the new task's `attachments/`
  on save. The modal shows them as already-attached chips; the user can
  remove individually. Copying (not linking) keeps the new task
  self-contained when the planning folder eventually archives.
- **Kind**: defaults to `coding`, the user can change.
- **State**: defaults to `2-ready` so the new task picks up next.

The cleanest UI hook is a per-task action visible only when
`kind = planning` and the latest run has finished successfully:
"Promote to coding task." The backend endpoint takes the source job key
and returns a fully-populated `CreateJobRequest` payload; the frontend
opens the existing create-task modal with that payload, so the modal
stays the single source of truth for create UX.

For Research tasks, this button is absent by design. The user said the
result is "just" a report; the only verb is "read."

## Tool surface differences

- **Coding**: existing CLI invocation. Working dir is the project tree.
  Web search off (current default; Claude has `WebSearch` and `WebFetch`
  but the runner does not enable them today).
- **Planning**: same working dir, web search off. Stronger "do not write
  source" framing in the bootstrap prompt. Read-only tools only.
- **Research**: working dir is the project tree (so the agent can ground
  findings in code), but **web search and web fetch are enabled** by
  default. This is the one differentiator the user explicitly called
  out. Same read-only framing.

The framing lives in `prompts/runtime/`, parameterized by kind. The
existing structural unit-test guards extend to the new templates.

## What this means for the protocol pane

Run cards stay the same shape. The kanban tile gains a small kind icon
(coding = pen, planning = compass, research = magnifier) so the user can
see at a glance why a project has three tasks running concurrently.

`status.md` is generated as today; the per-kind prompt produces different
content and the protocol pane just renders it.

## What this is *not*

- Not a workflow engine. The board does not chain a research task into a
  planning task into a coding task. The user does that manually with the
  promote button (planning → coding) and copy-paste (research → planning,
  if they want).
- Not a separate queue. Same lanes, same ordering, same review pipeline.
  Read-only tasks still go to `4-auto-review` / `5-human-review`; the
  human just rubber-stamps faster because nothing changed on disk.
- Not multi-agent. One CLI per task. Parallelism is across tasks, not
  inside a task.
- Not a relaxation of the cross-project parallelism story. Different
  projects still run independently. This change is scoped to "what
  happens inside one project."

## Decisions (2026-05-14)

The five open questions from the initial draft are resolved below. These
are documented defaults, not consensus from a separate review round; any
of them can be revisited by the implementation task that lands the
runner change.

1. **Soft parallelism cap default: 4, configurable per project.**
   The setting key is `ReadOnlyParallelism` on the project config.
   Default 4 protects against an accidental fork bomb when the user
   queues many planning runs in a hurry, while staying well above the
   normal working set of "a couple of parallel investigations." Users
   who want "no cap" set a high value (e.g. 32); we do not introduce a
   separate unlimited mode because the OS/quota backpressure is a fuzzy
   signal and a soft cap is easier to reason about.
2. **Web search: per-task toggle, defaults differ by kind.** The create
   modal exposes a single "Allow web access" checkbox. Default *off*
   for Planning, default *on* for Research. The toggle is also present
   on Coding so library-docs lookups during implementation are
   available when needed (still off by default there). One control,
   three defaults, no special-case UI per kind.
3. **Promotion target state: `1-preparation`.** The promote-to-coding
   modal lands the new task in `1-preparation` so the user gets one
   review pass on the auto-filled prompt before the runner picks it up.
   The state field is editable in the modal; a user who trusts the
   plan can switch to `2-ready` before saving. Rationale: the
   brainstorm explicitly described "modal opens pre-filled, user hits
   Save" but did not promise pickup-on-save, and a `1-preparation`
   default is the conservative choice that costs at most one click.
4. **Image lifecycle: copy on promote.** Attachments are copied byte-
   for-byte from the planning job's `attachments/` and `results/` (image
   files only) into the new task's `attachments/`. No hard-links, no
   symlinks. Reasons: hard-links are not portable across filesystems
   and break when the planning folder archives; Windows symlinks need
   privilege the dev box does not always have; copying makes the new
   task self-contained for archival, export, and future rehydration.
5. **Quota gating: never preempt, refuse new read-only pickups instead.**
   An in-flight planning or research run always runs to completion.
   When the CLI quota probe is below the warn threshold *and* at least
   one coding task is queued, the read-only picker pauses new pickups
   until the coding slot frees. This is a one-way valve: read-only
   yields to coding under pressure, coding never yields. The quota
   threshold reuses the existing supervisor probe; no new probe path.

## Suggested next steps

Now that the decisions above are pinned, implementation can phase as:

1. **ADR for the carve-out.** Documents that the intra-project
   parallelism non-goal applies to mutating work only, and that
   read-only kinds (Planning, Research) are exempt because the cause
   the non-goal protects against (working-tree collisions) does not
   apply to them. Without this ADR, the next agent will revert the
   carve-out on sight. AGENTS.md gets the wording change in the same
   commit.
2. **`kind` field landing, no behavioural change.** Add `kind` to
   `JobInfo` and `CreateJobRequest`, default `"coding"`, ignored by the
   runner for now. Boot-time migration tags legacy folders. UI shows
   the kind icon on the kanban tile. Reversible if we change our mind.
3. **Create-task modal: kind selector + web-access toggle.** Mockup
   first under `docs/mockups/`. Per-kind default prompt scaffolds live
   in `prompts/runtime/`. Sentinel-driven unit tests cover the new
   templates.
4. **Runner change behind a config flag.** Replace `_activeJobId` with
   `_activeCodingJobId` and a read-only execution lane gated by
   `ReadOnlyParallelism`. Quota-gating logic per decision 5. Ship
   behind a `enableReadOnlyParallelism` flag, default off, until shake-
   out is done. Promote-to-coding endpoint lands in the same phase.

Each of those is a separate task. They are not blocked on further
input from the user; they are blocked only on the user's go-ahead to
start implementation.

Coding work is intentionally out of scope of *this* task; the planning
deliverable is this note. The follow-up tasks above are the concrete
units that should be created when implementation starts.
