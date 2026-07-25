# Model Escalation and Companion Routing

Status: Proposed feature concept, no implementation

Date: 2026-07-23

Scope: Agent Studio task routing, CORE execution, review, and reissue policy

## Decision summary

Agent Studio should not adopt either pattern as one global default.

The recommended product policy is:

1. **Use qualification-first routing for every unpinned card.** TE-7, once
   available, should become the normalized task-class and risk producer.
   Today's deterministic `ModelQualificationService` remains the bootstrap
   producer.
2. **Make a one-step escalation cascade default-eligible only for small,
   bounded, reversible tasks with a strong independent completion oracle.**
   The default must remain off until a measured pilot demonstrates a positive
   cost and quota margin without a quality regression.
3. **Route large or high-risk tasks directly to the strongest trusted rung.**
   Spending an economy attempt first is false economy when the cost of a bad
   change or the probability of escalation is high.
4. **Keep companion execution opt-in by task class.** The best first companion
   shape is a bounded strong-model plan, economy-model execution, and the
   existing strong-model review. It suits long, mechanical execution after a
   short, high-value reasoning phase. It is not a general coding default.
5. **Treat grades as evidence, not authority.** A grade D can corroborate an
   upgrade decision, but no A-D grade may trigger a reissue by itself. This
   preserves the current contract that quality grades are reporting evidence,
   not lane gates.

The first implementation target should be escalation, not a nested
orchestrator-subagent system. Escalation reuses the existing attempt-chain,
reissue, worktree, pipeline, usage, and review machinery. Companion execution
adds a new role handoff and therefore needs a larger contract.

## Why the defaults differ

The relevant decision is not "small model or large model." It is whether the
task has a low-cost, reliable way to detect that the first model was
insufficient.

| Task class | Initial route | Automatic upgrade | Companion |
|---|---|---|---|
| Localized documentation, copy, test addition, UI polish, or mechanical edit with deterministic checks | Economy rung | Default-eligible after pilot, maximum one upgrade | Usually unnecessary |
| Scoped bug or feature within one or two well-tested surfaces | Qualification result | Opt-in until class-specific data proves a margin | Strong plan plus economy execution can be useful when implementation is mostly mechanical |
| Architecture, cross-cutting changes, state machines, concurrency, security, permissions, schema or data migration, release and merge logic | Strongest trusted rung | No economy attempt first | Optional strong planner/reviewer, but not an economy CORE default |
| Broad planning or research with independent read-only branches | Strong synthesis model | Not a coding reissue cascade | Bounded lead-worker pattern can be useful |
| Missing dependency, permission failure, quota exhaustion, host failure, or a real user decision | No capability escalation | Never | Never as an automatic recovery |
| Explicitly model-pinned card | Pinned route | Off unless the operator explicitly allows upgrades from the pin | Explicit only |

This classification should be owned by the referenced
`model-routing-richtlinie-wiki`, not copied into several runner branches. The
runner should consume a versioned route plan that contains the class, risks,
allowed rungs, and reason.

## Pattern definitions and boundaries

### Escalation cascade

An escalation cascade starts CORE on the lowest trusted rung selected by task
qualification. When independent evidence says that capability was insufficient,
the existing reissue path starts a new attempt on the next stronger live
catalogue rung.

It is not:

- a retry of the same model;
- quota fallback;
- a response to an environmental failure;
- an unlimited model ladder;
- permission to discard the prior worktree or evidence;
- a silent mutation of the card's configured model.

The first release should permit at most one automatic upgrade in an attempt
chain.

Names such as `luna` and `terra` are deployment or catalogue examples, not
policy constants. The route must select capability rungs from the live model
catalogue so a provider rename or entitlement change does not require a Studio
release.

### Companion model

A companion workflow gives different bounded roles to different model rungs
around one task artifact. Agent Studio should manage the roles sequentially so
their prompts, budgets, evidence, and model choices remain visible.

Three variants are materially different:

| Variant | Assessment |
|---|---|
| Strong plan, economy execution, strong review | Best companion candidate. It concentrates strong-model tokens in bounded reasoning and verification calls while leaving long mechanical tool work to the economy model. |
| Economy draft or execution, strong verify | Already close to the current pipeline when an economy CORE run is followed by the flagship quality grade. Useful as "companion-lite," but verification alone cannot repair the change, so a failed verification still needs an upgraded reissue. |
| Economy plan, strong execution | Weak default. The strong executor must validate or redo a possibly weak plan, so the cheap planning call adds handoff risk without removing much strong-model work. |

A CLI-native lead agent spawning hidden subagents is not the first product
slice. It would obscure per-role usage and route provenance, and it risks
crossing the product boundary that the runner and pipeline own git and attempt
state. A later bounded read-only research fan-out can be considered separately.

## Fit with the existing pipeline

The current system already provides most of the escalation substrate:

- `pre-model-qualification` classifies a card and selects from the live CLI
  catalogue without hardcoded model ids.
- Explicit model and thinking-level pins win over qualification.
- `ProjectRunner` records qualification decisions and actual outcomes,
  including token usage and attempt number.
- Auto-review reissues move the card to `2-ready` at order 0 and preserve the
  task worktree, branch, commits, results, follow-up history, and attempt chain.
- A reopened task reruns PRE, CORE, and POST instead of flattening prior
  evidence.
- The shared reissue budget is already scoped to one attempt chain.
- Environmental failures already follow a separate retry and escalation
  taxonomy and do not charge the task's reissue budget.
- The automatic A-D quality grade already uses a quality-first model, but is
  deliberately reporting-only.

The missing seam is an **attempt-scoped model route**. Today a reissue moves and
steers the same card, but it does not record "use the next stronger rung for
this attempt." The card's explicit model flags survive the move, so a normal
reissue tends to select the same model again.

### Target flow

```text
TE-7 / ModelQualification
        |
        v
versioned route plan
  direct | cascade | companion
        |
        v
CORE attempt on selected rung
        |
        v
typed outcome + deterministic gates + review evidence
        |
        v
pure escalation decider
  accept/post-process | upgrade reissue | human review
        |
        +--> upgrade reissue: 2-ready order 0, fresh model session,
                              same worktree and evidence, next trusted rung
```

The upgraded attempt should start a fresh CLI session when the model changes.
It should receive the original task, the prior diff and commits, the exact
failing gate evidence, and the versioned orchestrator follow-up. Reusing the
worktree means the first attempt is not necessarily discarded, but its tokens
and elapsed time are still sunk and partial work may require cleanup.

The attempt override must not rewrite the card's configured model. This mirrors
quota fallback, which is run-scoped and visible. The next human-created attempt
or new chain can return to normal qualification.

### First-slice placement

The low-risk first slice should make the upgrade decision in the existing final
review-decision path. It reuses all current evidence and avoids reordering the
pipeline. Its cost model must therefore include the existing aspect and grade
calls made on the economy attempt.

If measurements show that this review fan-out consumes a material part of the
savings, a later optimization may add an early
`post-model-escalation-decision` after the early completeness and deterministic
build/test evidence but before expensive semantic reviews. That change should
be separate because it alters which rows are skipped on the first attempt and
which attempt receives the final quality grade.

## Escalation signals

The binding decision should follow the existing ADR-0032 discipline: models
classify, a pure rule engine decides.

### Eligible signals

An upgrade is eligible only when the current rung is below the route plan's
maximum trusted rung and at least one capability signal is present:

1. A new typed executor signal such as `model-escalation-request`, with a
   bounded reason and evidence reference. It must not reuse
   `TASK_NEEDS_INPUT`, which means that a real user decision is required.
2. A deterministic code-defect gate failure tied to the current attempt,
   subject SHA, gate id, and failure fingerprint.
3. An abort-review `stronger-reissue` recommendation when the host taxonomy
   says the run drifted, looped, or misunderstood the task rather than failed
   environmentally.
4. A blocking aspect finding tied to a concrete requirement or defect.
5. A grade D as corroboration for one of signals 1 through 4.

The pure decider should require:

- route mode `cascade`;
- a strictly stronger live and trusted rung;
- no prior model upgrade in the current attempt chain;
- remaining shared reissue budget;
- a non-environmental outcome;
- fresh evidence from the current attempt.

### Signals that must not upgrade a model

- grade C alone, because an unparseable grade intentionally falls back to C;
- grade D alone, because grades are currently advisory;
- grade service failure or an unparseable reviewer response;
- quota, authentication, permission, worktree, network, host-load, or CLI
  launch failure;
- missing external dependency;
- context overflow;
- a genuine `TASK_NEEDS_INPUT` decision;
- repeated failure after the upgraded attempt.

After one upgraded attempt, unresolved capability findings route to human
review. They do not start a second automatic ladder climb.

### Grade interaction

| Grade evidence | Escalation meaning |
|---|---|
| A or B | Strong evidence against a capability escalation for soft concerns. A deterministic red gate still wins. |
| C | Advisory only. It may prompt inspection but never an upgrade by itself. |
| D | Corroborates a concrete gate, aspect, or executor capability signal. Never sufficient alone. |
| Missing, failed, or unparseable | Environmental or unknown review evidence. Never a statement about CORE capability. |

This keeps the quality grade useful without reviving the known false-reissue
problem where broad review wording overruled actual artifacts.

## Cost, quota, and latency model

There is not yet enough production evidence in this checkout to claim a
realized saving. `ModelQualificationService` currently emits a heuristic
estimated saving of 10 to 65 percent relative to the top live rung. This is not
a measured portfolio result.

Use these variables per task class and route:

- `C_e`: economy attempt cost, including PRE, CORE, and any POST work completed
  before the upgrade decision;
- `C_d`: direct strong-model task cost;
- `C_g`: incremental classifier, gate, queue-admission, and duplicated
  post-processing cost caused by escalation;
- `C_u`: upgraded attempt cost after carrying prior artifacts forward;
- `p`: observed fraction of economy attempts that escalate.

Expected cascade cost is:

```text
E[C_cascade] = C_e + p * (C_g + C_u)
```

The cascade is cheaper than direct strong execution only when:

```text
p < (C_d - C_e) / (C_g + C_u)
```

The same equation must be evaluated in both dollars and quota units. Dollar
prices do not represent subscription window pressure when the small and large
models use different quota buckets.

### Illustrative break-even, not a forecast

Normalize a direct strong task to `C_d = 1.00`. Assume an economy attempt costs
`C_e = 0.25`, escalation overhead costs `C_g = 0.05`, and the upgraded attempt
costs `C_u = 1.00`.

| Escalation rate `p` | Expected cascade cost | Saving vs direct strong |
|---:|---:|---:|
| 10% | 0.355 | 64.5% |
| 25% | 0.513 | 48.8% |
| 50% | 0.775 | 22.5% |
| 70% | 0.985 | 1.5% |

The break-even rate is about 71.4 percent in this example. A production default
needs a safety margin, not a result barely below break-even. The recommended
promotion rule is that the upper confidence bound of the observed escalation
rate remains at least 20 percent below the class-specific break-even threshold.

### What is really lost on escalation

The first run's token and quota spend cannot be recovered. Its wall-clock time,
runner slot, and any queue wait are also lost from the user's latency budget.
The upgraded model may additionally spend tokens understanding or cleaning up
partial work.

What can be reused is the worktree, commits, diff, test output, results, and
failure diagnosis. `C_u` may therefore be less than a direct from-scratch
strong run, but that must be measured rather than assumed.

For elapsed time:

```text
E[T_cascade] = T_e + p * (T_gate + T_ready_queue + T_u)
```

Every escalated task has worse latency than routing it directly to the strong
model. The portfolio can still improve throughput when most tasks finish on the
economy rung or when the rungs consume separate quota windows. The UI should
show both the portfolio saving and the individual escalated-task delay.

## Route and evidence contract

A versioned `ModelRoutePlan` should be resolved before CORE and referenced by
every attempt. Conceptually it contains:

- policy version and decision id;
- task class, qualification confidence, and risk flags;
- route mode: `direct`, `cascade`, or `companion`;
- initial CLI, model rung, and thinking level;
- maximum trusted rung and allowed CLI transitions;
- whether an explicit card pin permits automatic upgrade;
- reason and source: TE-7, current qualification, project policy, or card
  override.

Each attempt should record:

- route-plan id, attempt-chain id, and attempt id;
- actual CLI, model, thinking level, and live-catalogue provenance;
- rung index and whether the selection was initial, quota fallback, manual, or
  model upgrade;
- parent attempt and inherited evidence references;
- escalation trigger, trigger evidence, and decider version;
- whether the shared reissue budget was charged;
- tokens, historical price status, wall time, gate time, ready-queue time, and
  terminal outcome.

The existing `model-qualification.jsonl` decision and outcome stream is the
natural benchmark surface. It can be extended or joined by stable decision ids,
but it must remain append-only and retain unknown-price state.

## Companion pipeline contract

The recommended companion pilot is sequential:

```text
strong bounded plan -> economy CORE -> deterministic gates
                    -> strong review -> accept or upgraded reissue
```

The plan must be an immutable artifact, not hidden session context. It should
contain scope, constraints, ordered changes, verification commands, known
risks, and explicit decisions left to the executor. The economy executor may
deviate only when it records why. The reviewer receives the plan, actual diff,
results inventory, and deviation list.

Budgets are per role:

- planner: short wall-clock and output cap, strong trusted rung;
- executor: normal tool budget, economy or middle rung;
- reviewer: bounded one-shot, strong trusted rung;
- rework: at most one upgraded CORE attempt.

Companion is justified only when the strong bounded calls cost materially less
than moving the full tool-running CORE workload to the strong model. This is
most plausible for mechanical execution with expensive repository traversal or
test loops. It is least plausible when reasoning and execution are tightly
interleaved.

## Relationship to referenced patterns

The PROJ-021 pattern names should map to this feature as follows:

| ai-patterns.dev entry | Agent Studio use |
|---|---|
| `model-task-capability` | Supplies evidence about which rung can perform which task class. |
| `model-trust-map` | Caps which model may plan, mutate, verify, or make a binding recommendation for each risk class. |
| `orchestrator-subagent` | Describes role delegation, but does not itself authorize hidden nested execution in Studio. |
| `escalation-cascade` | Defines the low-rung attempt, evidence-based deferral, and strictly stronger retry pattern. |
| `companion-model` | Defines bounded cross-model roles around one shared artifact. |

`ai-patterns.dev` was not DNS-resolvable from this runner on 2026-07-23, and no
PROJ-021 checkout was available locally. The supplied slugs are therefore
treated as reference contract names, not as verified quotations. A follow-up
must reconcile this page with the actual entries and add reciprocal links.

External primary references support the general patterns but do not establish
Agent Studio defaults:

- [Language Model Cascades: Token-level uncertainty and beyond](https://openreview.net/forum?id=KgaBScZ4VI)
  shows that deferral quality depends on the uncertainty rule, and that naive
  generative sequence uncertainty has length bias.
- [Cluster, Route, Escalate](https://arxiv.org/abs/2606.27457) separates
  pre-routing from post-generation quality estimation and tunes the operating
  point on task-correctness labels.
- [Anthropic's multi-agent research system](https://www.anthropic.com/engineering/multi-agent-research-system)
  reports value from a strong lead with smaller workers on breadth-first
  research, while explicitly scaling token use. That result does not imply a
  coding default.

## Rollout and default-promotion gates

1. **Observe.** Record route plans and counterfactual recommendations without
   changing the execution model.
2. **Shadow evaluate.** Compare matched task classes, final acceptance, reissue
   rate, critical escaped defects, token and quota use, cost, and latency.
3. **Opt-in pilot.** Allow one project to enable cascade for an explicit
   low-risk class. Keep card pins authoritative.
4. **Class default.** Promote only the qualifying class, never the whole
   workspace.
5. **Companion pilot.** Start only after escalation telemetry can attribute
   per-role and per-attempt cost.

Minimum promotion evidence:

- enough completed attempts to cover each enabled class and model pair, with a
  recommended floor of 100 eligible attempts overall and at least 30 per class;
- no critical escaped defect increase;
- no material reduction in accepted-without-rework quality against a matched
  direct-strong baseline;
- at least 20 percent portfolio cost or constrained-quota saving;
- escalation-rate upper bound safely below break-even;
- false upgrades below 10 percent after human review;
- latency and queue impact visible separately for economy successes and
  escalated tasks.

## Effort estimate

Estimates are engineering days for one engineer, excluding calendar time needed
to collect a representative production sample.

| Slice | Size | Estimate | Notes |
|---|---:|---:|---|
| Baseline dataset and route economics report | M | 3 to 5 days | Reuse qualification outcomes, CAR historical pricing, quota snapshots, and pipeline timing. |
| Versioned route-plan and attempt provenance contract | M | 3 to 5 days | Backend records, schemas, compatibility, append-only telemetry. |
| Live-rung upgrade resolver and model-upgrade reissue | L | 5 to 8 days | Attempt-scoped override, fresh session, same worktree, strict stronger-rung rule. |
| Pure escalation decider and trigger adapters | M | 4 to 6 days | Outcome taxonomy, gate and aspect evidence, grade corroboration, budget caps. |
| Timeline, task-detail route chain, and operator controls | M | 3 to 5 days | Reason, model chain, cost and delay; both themes and Playwright proof. |
| Shadow and opt-in pilot controls | M | 4 to 6 days | Feature flags, cohort comparison, promotion report. |
| Companion role and immutable-plan contract | L | 6 to 10 days | New role handoff, budgets, artifact schema, deviation record. |
| Companion pilot wiring and evaluation | L | 8 to 12 days | Strong plan, economy CORE, strong review, rework route, UI attribution. |
| PROJ-021 pattern reconciliation | S | 1 to 2 days | Verify entries and add reciprocal links once the project is reachable. |

The escalation pilot totals roughly 22 to 35 engineering days. The companion
pilot adds roughly 14 to 22 days. These are not one release commitment: the
measurement slice can invalidate or narrow later slices before implementation
cost is spent.

## Recommended follow-up cards

### ESC-1: Build the model-route baseline

Produce a read-only cohort report by task class, initial model, grade, gate
outcome, reissue count, final human disposition, tokens, historical cost, quota
window, and phase latency. Calculate class-specific break-even thresholds.

Acceptance: the report distinguishes unknown price from zero, environmental
retries from capability reissues, and first attempts from upgraded attempts.

### ESC-2: Define route-plan and attempt provenance schemas

Specify the versioned route plan, attempt selection source, model rung, parent
attempt, evidence references, and upgrade reason. Extend the qualification
benchmark stream without rewriting history.

Acceptance: a reader can reconstruct why every attempt used its actual model
without reading free-form logs.

### ESC-3: Add an attempt-scoped stronger-model reissue

Resolve the next trusted live rung, move through the existing `2-ready` order-0
path, start a fresh model session, preserve worktree evidence, and leave the
card's configured model unchanged.

Acceptance: the second attempt is provably stronger, one upgrade is the hard
cap, and explicit pins block it unless opted in.

### ESC-4: Add the pure escalation decider

Combine current-attempt outcome taxonomy, deterministic gates, aspects, typed
executor capability request, grade corroboration, and reissue budget.

Acceptance: exhaustive table tests prove that environmental faults, C/D grade
alone, dependencies, permissions, quota, and user questions never trigger a
model upgrade.

### ESC-5: Expose the model route and economics

Show initial and upgraded rungs, trigger, evidence, reissue-budget charge,
per-attempt cost, gate time, ready-queue time, and aggregate saving or loss.

Acceptance: users can distinguish quota fallback, same-model reissue, and
capability upgrade without opening raw logs.

### ESC-6: Run a shadow and opt-in pilot

Start with localized low-risk tasks that have deterministic checks. Publish the
promotion metrics and retain a per-project kill switch.

Acceptance: no class becomes default until it passes the promotion gates in
this document.

### COM-1: Define the companion role contract

Create the immutable plan, executor deviation, reviewer input, role budget, and
model trust contracts. Keep roles sequential and Studio-visible.

Acceptance: every role's prompt, model, usage, artifact, and decision is
attributable to one attempt chain.

### COM-2: Pilot strong-plan, economy-CORE, strong-review

Enable the companion flow for one mechanical, test-backed class and compare it
with direct strong and escalation-only cohorts.

Acceptance: the pilot proves that bounded strong calls save constrained strong
quota and do not merely add two more model calls.

### PAT-1: Reconcile PROJ-021 patterns

Verify and update `model-task-capability`, `model-trust-map`,
`orchestrator-subagent`, `escalation-cascade`, and `companion-model`; add links
to the Agent Studio route contract and the measured pilot.

Acceptance: pattern descriptions and Studio policy agree on triggers, trust,
budgets, and non-goals.

## Living knowledge log

- **2026-07-23:** Initial concept. Recommendation is no global default,
  class-scoped escalation after measurement, direct strong routing for
  high-risk work, and companion execution only as an opt-in role pipeline.
