# Token-budget load management — spend the budget, never hit the wall

**Status:** concept v2, 2026-07-22. Operator-directed after two quota walls
in 24h (night: claude dry -> 32 cards parked; noon: codex 5h window dry ->
launch-failure cascade). Related:
[`orchestrator-in-app.md`](orchestrator-in-app.md) (transparency layer),
[`publishing-workflows.md`](publishing-workflows.md) §9 pattern, AGT-2040
(fallback map, merged), AGT-2055 (quota-aware admission).

## 1. The framing (operator, verbatim anchors)

- "Ich habe ein **Token-Budget** und ich möchte es **effizient ausnutzen**."
- The load algorithm must be an **algorithm, not a CLI call**. Never learn
  about a dry quota by burning a launch (and a reissue budget) against it.
- Two axes, deliberately separate: **Calculation** (what do tokens cost —
  the pricing library, CAR-3, shipped) and **Selection** (which model do I
  pick for this task under this budget — new, also runner-library
  knowledge).

## 2. The control loop

Every admission decision runs through:

1. **State**: quota snapshots per CLI (5-hour and 7-day windows, reset
   times — both are first-class inputs).
2. **Projection**: burn rate × remaining window time vs. remaining budget.
   5h projection steers immediate admissions; 7d projection steers
   longer-horizon choices (e.g. stop burning the weekly window on bulk
   maintenance runs).
3. **Decision**, in this order:
   a. **nearby-reset wait** - if the opt-in wait policy is enabled, the primary
      is strictly capped, and its confirmed reset is below the resolved global
      or project threshold, keep the requested model and wait visibly.
   b. **normal** - when the primary is healthy, launch with the card's model.
   c. **switch** - an actual or projected wall outside the nearby-reset branch
      uses the fallback map (AGT-2040), including cross-CLI.
   d. **downshift** - budget pressure plus a light task may select a smaller
      model per the efficiency matrix in section 3.
   e. **throttle** - if no usable model switch exists, reduce parallel
      admissions so the projection clears, but never throttle to zero.
   f. **exhausted wait** - if every option is dry, remain in Ready with a
      visible reason and next reset. No launch attempt, budget burn, or
      escalation occurs.
4. **Event**: every non-normal decision is a logged orchestrator event
   with the numbers that drove it (burn rate, remaining budget/time,
   chosen action). Silence is forbidden.

The nearby-reset branch is backed by CodingAgentRunner 0.6.0. Its
`QuotaWaitStarted` and `QuotaWaitEnded` events project into the same durable
substate pattern as Run-Liveness: `quota-wait.json`, lifecycle phase
`quota-waiting`, task timeline decisions, and a live board-card countdown. The
marker is cleared after a refreshed quota snapshot or when the library resumes
the same request.

## 3. Token-efficiency matrix (Selection axis → runner library)

"Was kriege ich für meine Tokens?" — a model-selection knowledge base in
**coding-agent-runner** (beside the pricing catalog, same spirit: one
tested, versioned source):

- Per model: capability tier, cost class (from the pricing lib), typical
  effort levels, suitability by task class (heavy design vs. mechanical
  chore vs. doc edit).
- API sketch: `SuggestModel(taskClass, budgetPressure, available)` →
  ranked candidates with rationale string (the rationale goes into the
  orchestrator event).
- The matrix is data + pure functions — the *policy* (when to downshift)
  stays in the Studio's admission algorithm.

## 4. The transparency view (own surface)

A dedicated **Lastverteilung** view in the orchestrator transparency layer:
current windows (5h/7d per CLI) with burn-down and projection lines, the
decision-event stream (switch/downshift/throttle/wait with reasons),
per-model spend vs. what the pricing lib says it bought. This is where the
operator audits "wurde mein Budget effizient ausgenutzt?".

## 5. Slices

| Slice | Scope | Where |
|---|---|---|
| AGT-2055 | admission algorithm: pre-launch check, projection, switch/throttle/wait, events | Studio (ready) |
| CAR-4 | token-efficiency matrix + SuggestModel API | runner lib (ready) |
| AGT-2056 | Lastverteilung transparency view over the decision events | Studio (after 2055) |
