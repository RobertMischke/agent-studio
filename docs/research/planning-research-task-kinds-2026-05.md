# Planning and Research task kinds - design exploration (2026-05)

Status: reconciled with ADR-0052 (2026-05-31). No code change. Captures
user intent from the `planning-task-task-differenzierung` brainstorm.
The parallelism *mechanics* this note originally had to argue for now
live in [ADR-0052](../architecture/decisions/adr-archive.md#adr-0052---intra-project-parallelism-is-now-an-opt-in-orchestrator-gated-capability-2026-05-31)
and the full design in [docs/concepts/parallel-task-execution.md](../concepts/parallel-task-execution.md).
What remains genuinely this note's own is the **task-kind taxonomy**
(planning / research / coding as a first-class `kind`), the **read-only
pipeline** those kinds run, and the **promote-planning-result-to-coding-
task** flow. Those are framed below as additive on ADR-0052. The
previously-open design choices are pinned as documented defaults at the
end, re-validated against the new model.

## What the user asked for

Three task kinds, distinguished by what the agent is allowed to do and what
the user does with the result:

1. **Coding task** (today's default): mutates source. Its parallelism and
   git isolation are governed by ADR-0052 (worktree + `task/<id>` branch,
   orchestrator-gated, `maxParallelism`).
2. **Planning task**: read-only. Analyzes the codebase and proposes the next
   concrete piece of work. Many should be runnable in parallel. No execution
   limit (no timebox, no token cap from the orchestrator). The result is the
   point. The user wants a one-click flow to take a finished planning result
   and turn it into a new coding task: the create-task modal opens
   pre-filled, attached images carry over, the user just hits Save.
3. **Research task**: read-only. Broader than planning - finds things out,
   possibly via web search. Many parallel. No follow-up action implied; the
   deliverable is a clean report the user reads.

Planning and Research differ by *intent and tool surface*, not by execution
mechanics: planning targets "what should we build next in this codebase,"
research targets "what is true about a topic" and may want web search.

## Premise correction (2026-05-31): the non-goal is already gone

The original version of this note spent its longest section arguing that
the intra-project-parallelism non-goal needed a *carve-out* for read-only
kinds, and that an ADR had to be written before any code change. **That is
now obsolete.** ADR-0052 (Accepted, 2026-05-31) reversed the non-goal
outright - not a carve-out, a full reversal - and the enforcing guards
(`IntakeRunner.CheckBlocked`, `OrchestratorPrepRules.HasOutOfScopeToken`)
are already removed. The full parallelism design lives in
[docs/concepts/parallel-task-execution.md](../concepts/parallel-task-execution.md).

So there is no policy battle left to fight here. What ADR-0052 does *not*
cover, and what this note still owns:

- **Task kinds as a taxonomy.** ADR-0052 parallelises *coding* tasks. It
  says nothing about a `kind` field or about planning / research as
  distinct kinds with a different tool surface and a different pipeline.
- **The read-only pipeline.** A planning or research run produces a
  *report*, not a worktree diff. It should skip the git pre/post steps
  (worktree-create, Commit+Push, Integration, teardown) entirely - there
  is nothing to commit. ADR-0052's machinery is for tasks that mutate the
  tree; read-only kinds opt out of it.
- **Promote-planning-result-to-coding-task.** The headline interaction
  (read the plan, hit a button, get a pre-filled create modal, save) is
  unique to this note.

The rest of this document is therefore reframed as **additive on
ADR-0052**: it defines the kinds and how the read-only ones dock onto (and
mostly opt out of) the parallel-execution machinery.

### How read-only kinds dock onto ADR-0052

ADR-0052 gives each running task a slot, a worktree, a `task/<id>` branch,
and a git pre/post pipeline. Planning and Research reuse the *slot* concept
(they consume a CLI process and quota) but **opt out of the git steps**:

- **No worktree, no branch.** A read-only run executes against a read-only
  checkout of `integrationBranch`; it never creates `task/<id>`, never
  commits, never integrates. The pre-step that would create a worktree and
  the post-steps that commit / merge / tear down are skipped for these
  kinds.
- **Always `parallel-ok` in the gate.** ADR-0052 §5's parallelisability
  gate compares predicted file scopes. A read-only kind touches no paths,
  so it is unconditionally `parallel-ok` and never `exclusive`. The gate
  can short-circuit on `kind ∈ {planning, research}` before it bothers
  computing scope.
- **Still slot- and quota-bounded.** They are not "free." They occupy a
  worker slot and consume tokens, so they count against `maxParallelism`
  and the per-project quota budget (ADR-0052 §9 D5). "No limit on what a
  planning task may *do*" (the user's words) means no timebox and no
  orchestrator-imposed scope cap on the run itself - not an unbounded
  number of concurrent planning processes.

## Data model implications

**Reconciliation (2026-05-31):** the Epics feature shipped first and took the
`kind` field for the *container* taxonomy (`task` | `epic`). The *execution*
taxonomy this note needs is orthogonal (a leaf task has an execution mode; an
epic is a container), so it lives in a **separate `mode` field** - not in
`kind`. No migration of the Epics `kind` values. **Landed 2026-05-31** on
`feature/task-modes` (behaviour-neutral foundation): `TaskModes`
(coding|planning|research) + `TaskInfo.Mode` + `AllowWebAccess`,
`CreateJobRequest.Mode`/`AllowWebAccess`, scanner + create persistence, unit
tests.

```jsonc
{
  "id": "...",
  "kind": "task" | "epic",                       // Epics (already shipped)
  "mode": "coding" | "planning" | "research",    // default "coding"
  "allowWebAccess": false,                        // default by mode (research = true)
  ...
}
```

`mode` defaults to `"coding"` so existing jobs and `CreateJobRequest` payloads
without the field keep working unchanged; the scanner normalises a
missing/unknown value to `coding`, so no boot-time migration is needed
(absence == coding). `TaskModes` mirrors `TaskKinds`.

The mode is a property of the task, not of the lane. All modes use the same
lifecycle states. The pipeline and the parallelisability gate branch on mode
(`TaskModes.IsReadOnly`).

This `mode` field is the behaviour-neutral first slice; it docks onto
ADR-0052's slicing plan the same way the `kind` field did.

## Runner / scheduler implications

The original version of this note proposed a *separate* read-only
execution lane to dodge the `_activeJobId` single-slot latch. ADR-0052
has since generalised that latch into N worker slots
(`ProjectRunner.cs:461`), so there is no separate lane to build - read-
only kinds ride the same slot model and differ only by which pipeline
steps run and how the gate treats them.

Concretely, dock onto ADR-0052 like this:

- **Slot admission.** A planning/research task takes a slot like any
  other. The pick-gate (ADR-0052 §5.2) short-circuits to `parallel-ok`
  for read-only kinds without computing scope, so they never block on a
  running coding task and a running coding task never blocks them.
- **Pipeline shape per kind.** ADR-0052 makes git a set of pre/post
  pipeline steps (ADR-0045). Read-only kinds run a pipeline with those
  git steps *omitted*: no worktree-create pre-step, no Commit+Push /
  Integration / teardown post-steps. What remains is "render prompt →
  run agent → produce report → render `status.md`." This is the cleanest
  expression of "read-only": not a guard that forbids writing, but a
  pipeline that has no commit step to begin with.
- **Containment, not trust.** A planning agent that "helpfully" edits a
  file would leave changes in the read-only checkout with no commit step
  to capture them. Surface any non-empty diff at run end as a hard
  violation on the timeline; do not auto-revert (the user decides). A
  sandboxed/throwaway checkout is the stronger option if `maxParallelism`
  ever lets a read-only run share a tree with a coding run - but with the
  git steps omitted there is no `task/<id>` worktree for them, so they
  read the integration checkout and must not be given a writable one they
  could integrate from.
- **Quota.** Read-only kinds consume the same CLI quota as coding and
  count against the per-project budget (ADR-0052 §9 D5). The picker
  refuses new read-only pickups when the budget is tight and a coding
  task is queued - see decision 5 below, which is the kind-aware
  specialisation of D5.
- **Broadcast fan-out.** N concurrent runs are already ADR-0052's world;
  the activity-log aggregator and SignalR broadcasts must not assume one
  active job. That is ADR-0052's concern, inherited here unchanged.

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
- **State**: defaults to `1-preparation` so the user gets one review pass
  on the auto-filled prompt before pickup (decision 3 below); editable in
  the modal.

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

## Decisions (pinned 2026-05-14, reconciled with ADR-0052 on 2026-05-31)

The five open questions from the initial draft are resolved below. These
are documented defaults, not consensus from a separate review round; any
of them can be revisited by the implementation task that lands the
runner change. Decisions 1 and 5 were re-expressed against ADR-0052's
`maxParallelism` / quota model; their substance is unchanged.

1. **Read-only parallelism is bounded by ADR-0052's `maxParallelism`,
   not a second knob.** The original draft proposed a standalone
   `ReadOnlyParallelism = 4`. With ADR-0052 that would be a confusing
   second cap. Decision: read-only kinds occupy the same worker slots as
   coding and are bounded by the project's `maxParallelism`. A project
   that wants planning/research to fan out wider than its coding
   concurrency may set an *optional* `readOnlyParallelism` override
   (default: unset = use `maxParallelism`); when set it is an additional
   pool of slots reserved for read-only kinds. The fork-bomb guard the
   original "4" was protecting against is now `maxParallelism` itself
   plus the quota budget (decision 5). Net: one cap by default, an
   override only for the project that actually needs asymmetric fan-out.
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
   This is the kind-aware specialisation of ADR-0052 §9 D5 (cap effective
   parallelism by the token/quota budget). An in-flight planning or
   research run always runs to completion. When the quota budget is below
   the warn threshold *and* at least one coding task is queued, the
   read-only picker pauses new read-only pickups until the coding slot
   frees. One-way valve: read-only yields to coding under pressure,
   coding never yields. Reuses the existing quota probe
   (`EnforceQuotaCapsOnActiveJob`); no new probe path.

## Suggested next steps

The parallelism mechanics are owned by ADR-0052's slicing plan
(`docs/concepts/parallel-task-execution.md` §8). What follows is the
*kind-specific* work that layers on top of it - not a competing plan.
Reconcile against the concept doc's §8 before spinning these out so a
slice is not built twice.

1. **`kind` field landing, no behavioural change.** Add `kind`
   (`coding` | `planning` | `research`, default `coding`) to `JobInfo`
   and `CreateJobRequest`; boot-time migration tags legacy folders; UI
   shows a kind icon on the kanban tile. This is the same
   behaviour-neutral first step ADR-0052's slice 1 already needs, so it
   should be folded into that slice rather than created as a rival task.
   *(The "write an ADR for the carve-out" step from the prior draft is
   deleted: ADR-0052 already reversed the non-goal - there is nothing
   left to carve out.)*
2. **Read-only pipeline variant. SHIPPED 2026-06-05** (see the ADR-0052
   amendment of the same date). The planning/research pipeline is the
   ADR-0045 pipeline with the git pre/post steps omitted (no worktree-
   create, no Commit+Push, no Integration, no teardown) and the
   parallelisability gate short-circuited to `parallel-ok`. Selection is
   `PipelineCatalogue.ForMode`; the git-step gate also lives in
   `TaskTransitionService`; the gate short-circuit is
   `ParallelSlotPolicy` (`TaskParallelism.ReadOnlyTask`). A non-empty diff
   at run end is reported as a containment violation rather than trusted
   or reverted (`ProjectRunner.ReportReadOnlyContainmentIfDirty` ->
   `read_only_containment_violation`).
3. **Create-task modal: kind selector + web-access toggle.** Mockup
   first under `docs/mockups/`. Per-kind default prompt scaffolds live in
   `prompts/runtime/`, parameterised by kind. Sentinel-driven unit tests
   cover the new templates. Web access (decision 2) is the one tool-
   surface differentiator ADR-0052 does not touch.
4. **Promote-planning-result-to-coding-task.** Backend endpoint takes the
   source job key and returns a fully-populated `CreateJobRequest`
   (title, prompt body from the `## Proposed task prompt` heading, copied
   image attachments, `kind = coding`, `state = 1-preparation`); frontend
   opens the existing create-task modal pre-filled. Visible only on a
   finished `kind = planning` task; absent on research. This is wholly
   this note's own scope - ADR-0052 has no equivalent.

Each is a separate task (or a fold-in to an existing ADR-0052 slice as
noted). They are not blocked on further input from the user; they are
blocked only on the user's go-ahead to start implementation, and slices
2+ inherit ADR-0052's gating on the crash-safe-pickup prerequisite.

Coding work is intentionally out of scope of *this* task; the planning
deliverable is this note.
