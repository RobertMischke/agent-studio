# Agent Contract Pattern

**The agent classifies. The rule engine decides.**

This is the foundational doc for [ADR-0032](architecture-decisions.md). It explains the three-zone shape that every agent-driven decision step in this repository must take, the contracts that flow across the boundaries, the deterministic decider table that maps the agent's output to a real action, and the two-sided loop guard that prevents cost or progress from running away. The first instance — pickup-failed diagnosis — is worked through end to end at the bottom.

The principle in one line: **safety- and cost-relevant choices live outside the model, by design.**

## The three zones

Every agent invocation is a structured RPC. The agent never speaks to side effects directly; both ends of the call are deterministic code that owns the contract.

```
                 ┌─────────────────────────────────────────┐
   Trigger ────► │           Rule Engine                   │
                 │  ┌───────────────────────────────────┐  │
                 │  │ Pre-Guard                         │  │
                 │  │ • attempts/job, attempts/run-set, │  │
                 │  │   token spend, wall-clock, age    │  │
                 │  │ • over budget? → ESCALATE,        │  │
                 │  │   no LLM call                     │  │
                 │  └─────────────┬─────────────────────┘  │
                 │  ┌─────────────▼─────────────────────┐  │
                 │  │ Builds <step>-input.json          │  │
                 │  │ (schema-validated)                │  │
                 │  └─────────────┬─────────────────────┘  │
                 └────────────────┼────────────────────────┘
                                  │
                                  ▼
                        ┌───────────────────┐
                        │   Agent (LLM)     │
                        │  - reads input    │
                        │  - reads evidence │
                        │  - returns typed  │
                        │    output only    │
                        └─────────┬─────────┘
                                  │
                                  ▼
                 ┌─────────────────────────────────────────┐
                 │           Rule Engine                   │
                 │  ┌───────────────────────────────────┐  │
                 │  │ Reads <step>-output.json          │  │
                 │  │ (schema-validated)                │  │
                 │  └─────────────┬─────────────────────┘  │
                 │  ┌─────────────▼─────────────────────┐  │
                 │  │ Decider                           │  │
                 │  │ Category × Confidence → Action    │  │
                 │  │ (fixed code table)                │  │
                 │  └─────────────┬─────────────────────┘  │
                 │  ┌─────────────▼─────────────────────┐  │
                 │  │ Post-Guard                        │  │
                 │  │ • action = requeue?               │  │
                 │  │ • same slug + category cycled     │  │
                 │  │   more than N times?              │  │
                 │  │ • → ESCALATE-HUMAN                │  │
                 │  └─────────────┬─────────────────────┘  │
                 │  ┌─────────────▼─────────────────────┐  │
                 │  │ Dispatcher                        │  │
                 │  │ • allow-listed self-heal commands │  │
                 │  │ • or transition lane              │  │
                 │  │ • or raise banner                 │  │
                 │  └───────────────────────────────────┘  │
                 └─────────────────────────────────────────┘
```

The agent can lie, hallucinate, or violate the schema. The rule engine must absorb that:

- Schema-invalid output → escalate-human, do not retry.
- Output outside the declared category set → escalate-human.
- Output suggesting an action outside the decider table → escalate-human.

Failing closed is the default. The agent can only steer toward outcomes the code already considers legal.

## Input contract

The Pre-Guard builds the input contract before any LLM call. The schema lives next to the consumer in [`docs/schemas/`](schemas/) and is loaded into the test matrix.

A typical input contract carries:

| Field | Purpose |
|---|---|
| `step` | A stable id of the agent step (e.g. `pickup-failure-diagnosis`). |
| `runId` | The orchestrator-issued run id; used as the run-folder prefix. |
| `evidenceRefs[]` | Paths or excerpts the agent may read, scoped to the run folder + read-only project state. |
| `priorAttempts[]` | What was tried before, with outcome category if known. Lets the agent see whether it is in a loop already, but the loop guard is separately enforced. |
| `budgets` | The remaining budget the Pre-Guard saw. The agent does not enforce these; they are echoed for diagnosability. |
| `categoryEnum` | The exact set of categories the agent is allowed to return. Out-of-set values are escalated. |

Both files are written to `<run-folder>/contracts/<step>-input.json` and `<step>-output.json` so every agent boundary is observable, replayable, and diffable across runs.

## Output contract

The agent must return a JSON object that validates against the step's output schema. Common shape:

```json
{
  "category": "<one of the allowed enum values>",
  "confidence": 0.0,
  "evidence": [
    { "kind": "log-tail", "ref": "logs/cli-output.log#L1234", "excerpt": "..." }
  ],
  "proposedAction": "<one of the allowed enum values, advisory only>",
  "selfHealCommands": ["<allow-listed command id>", "..."],
  "humanNote": "Optional short explanation surfaced in the UI banner."
}
```

`proposedAction` and `selfHealCommands` are **advisory**. The decider is allowed to ignore them and almost always does. They exist for diagnosability and to make the agent's intent reviewable, not because the rule engine trusts them.

## Decider table

The decider is a code table, not a config file, not a prompt. It maps `(category, confidence)` to exactly one action. The table is exhaustive: every category in the enum has at least one row, and the default for unmatched cells is `escalate-human`.

A decider for the pickup-failed step looks like this in pseudocode:

```csharp
public static PickupAction Decide(PickupFailureDiagnosis d) => d.Category switch
{
    "infra-cli-broken"  => PickupAction.HaltPipelineAndBanner,
    "infra-network"     => PickupAction.HaltPipelineAndScheduleRetry(TimeSpan.FromMinutes(5)),
    "task-bad-prompt"   => d.Confidence >= 0.8m
                              ? PickupAction.RequeueWithHumanReadableReason
                              : PickupAction.EscalateHuman,
    "task-env-missing"  => SelfHealAllowed(d.SelfHealCommands)
                              ? PickupAction.RunSelfHealAndRequeueOnce
                              : PickupAction.EscalateHuman,
    "transient"         => PickupAction.RequeueOnce,
    "unknown"           => PickupAction.EscalateHuman,
    _                   => PickupAction.EscalateHuman, // unknown enum value
};
```

Three rules apply to every decider:

1. **No network or filesystem inside the decider.** It is a pure function over the parsed contract. Side effects happen in the dispatcher one layer down.
2. **Confidence thresholds are fixed.** Tunables live in `appsettings.*` only when there is operator-meaningful policy variance; the default belongs in code.
3. **Decider tests are unit tests.** Every row in the table has at least one positive and one negative test in `backend.Tests/Decider/`.

## Self-heal allow-list

`selfHealCommands` is the only place in the contract pattern where the agent can influence what gets executed on the host. It is gated by an allow-list of stable command ids defined in `backend/Services/SelfHeal/SelfHealCommandRegistry.cs`. Each entry binds a command id to a small, audited shell or in-process action:

| Command id | Effect |
|---|---|
| `check-cli-shims` | Run [`tools/check-cli-shims.sh`](../tools/check-cli-shims.sh) to repair half-installed npm shims and orphaned postinstall stubs. Idempotent. |
| `git-fetch-and-prune` | `git fetch --prune` against `origin` in the workspace root. No working-tree mutation. |
| `restart-cli-quota-probe` | Bounce the in-process `QuotaService` so a failing-then-fixed CLI is observed sooner. |

Adding a new entry requires:

1. A code change to the registry with a one-line docstring.
2. An entry in [docs/loop-inventory.md](loop-inventory.md) if the command can re-enter a loop.
3. A unit test that constructs a `PickupFailureDiagnosis` with the new id and asserts the dispatcher runs it.

The dispatcher rejects any id not in the registry and writes a `selfHealCommandRejected` event to the run folder for observability.

## Loop guards: Pre and Post

A single failure mode forces both guards to exist:

- **Pre-Guard** without **Post-Guard** lets a confidently-wrong agent requeue the same job slug forever.
- **Post-Guard** without **Pre-Guard** lets a runaway pre-trigger fan-out into N concurrent agent calls before any of them returns.

Pre-Guard fields, evaluated in order, short-circuit on first hit:

| Field | Default | Effect when over |
|---|---|---|
| `attemptsPerJob` | 3 | Skip step; escalate-human. |
| `attemptsPerRunSet` | 8 | Halt all agent steps for this run set. |
| `tokenBudgetUsdCents` | 50 per run set | Halt with `over-budget` banner. |
| `wallClockSeconds` | 600 per run set | Halt with `over-time` banner. |

Post-Guard is keyed on `(slug, category)`. Default budget: 1 requeue per pair per 24 h. Counters are persisted to `<workspace>/state/loop-counters.json` (atomic-rename writes) so a backend restart does not silently reset them.

Both guards write a structured event to `<run-folder>/contracts/loop-guard-decisions.jsonl` whenever they refuse an action. The event is the audit trail.

## Loop inventory and architecture tests

[docs/loop-inventory.md](loop-inventory.md) is the registry of every place in the codebase where work can re-enter itself: retry, requeue, re-trigger, replay. Each entry names its kind (Pre-Guard / Post-Guard), the code anchor, the budget constant, and the breaker test.

CI runs three checks:

1. **`LoopInventoryConsistencyTest`** (every CI run, fast, no LLM): parses `loop-inventory.md`, follows each entry to its named code anchor and test, and fails if either is missing or out-of-date.
2. **Per-loop breaker tests** (every CI run, fast, no LLM): each entry's named test exercises the loop synthetically and asserts the breaker fires within budget.
3. **`LoopDiscoveryTest`** (`[Trait("Category","Weekly")]`, `Skip` unless `LOOP_DISCOVERY=1`, optionally LLM-driven): feeds the inventory plus the diff since the last green run to a discovery agent. The agent returns a `LoopCandidate.json` contract with proposed new entries; the test writes them to `docs/loop-inventory.md.candidates` and fails the run with a queue-task hint. The candidate file is committed by a human review, never by the test itself.

The discovery agent is itself bound by the contract pattern: input schema, output schema, no privileged execution.

## Worked example: pickup-failed

This is the first instance in production. It re-uses the existing dead-letter mechanism from ADR-0028 and adds a diagnosis step on top.

**Trigger.** `ProjectRunner.TryPickProgressJobOrDeadLetter` decides a slug has hit `PickupFailureThreshold` (default 3 silent runs) and moves the folder to `3a-failed-pickup/<slug>-pickup-failed-<utc-date>/`. Today this is where the loop ends; the new step kicks in here.

**Pre-Guard.** Before invoking the diagnostic agent:

- `attemptsPerJob` for `pickup-failure-diagnosis` step on this slug must be < 3. (A repeatedly-failing diagnosis is an infrastructure problem, not a job problem.)
- `attemptsPerRunSet` across all pickup diagnoses in the last hour must be < 8.
- Token budget for the diagnosis step is bounded at the equivalent of one Haiku call.

If any guard fires, the runner writes a `pickup-diagnosis-skipped` banner and stops the diagnosis. The dead-letter row in `pickup-failures.jsonl` already exists; nothing else changes.

**Input contract** (`pickup-failure-context.json`):

```json
{
  "step": "pickup-failure-diagnosis",
  "runId": "...",
  "jobSlug": "...",
  "cli": "claude",
  "attempts": [
    {
      "ts": "2026-05-06T22:55:30Z",
      "exitCode": 192,
      "runDurationMs": 14,
      "stdoutLines": 0,
      "stderrTail": "claude.exe is not a valid application for this OS",
      "headSha": "ba037cd..."
    }
  ],
  "cliPreflight": {
    "command": "claude --version",
    "exitCode": 1,
    "stderrTail": "...",
    "ranAt": "2026-05-06T22:55:32Z"
  },
  "evidenceRefs": [
    "logs/cli-output.log",
    "logs/pickup-failures.jsonl",
    "C:/Users/.../AppData/Roaming/npm/"
  ],
  "categoryEnum": [
    "infra-cli-broken",
    "infra-network",
    "task-bad-prompt",
    "task-env-missing",
    "transient",
    "unknown"
  ],
  "budgets": { "attemptsPerJobRemaining": 2, "tokenCentsRemaining": 50 }
}
```

**Agent.** A diagnostic agent reads the context, optionally inspects the referenced paths via read-only tools, and returns:

**Output contract** (`pickup-failure-diagnosis.json`):

```json
{
  "category": "infra-cli-broken",
  "confidence": 0.95,
  "evidence": [
    { "kind": "preflight",  "ref": "...", "excerpt": "claude.exe is not a valid application" },
    { "kind": "fs-listing", "ref": "...", "excerpt": ".claude-2shlnT4k orphan present in npm bin" }
  ],
  "proposedAction": "halt-pipeline",
  "selfHealCommands": ["check-cli-shims"],
  "humanNote": "claude.exe is the 500-byte stub from a broken postinstall."
}
```

**Decider.** Category `infra-cli-broken` always halts the pipeline, regardless of `proposedAction` or `confidence`. The decider also notes that `check-cli-shims` is allow-listed, so the dispatcher may run it before raising the banner — but only the decider, not the agent, decides whether to run it. The action that ships:

- Set the project's runner mode to `manual`.
- Write a high-severity banner to `<workspace>/logs/banners/pickup-infra-halt-<utc>.json`.
- Optionally invoke `check-cli-shims` self-heal once. If it succeeds and the next preflight passes, downgrade the banner to "halted, self-heal succeeded, manual resume required".

**Post-Guard.** Future automation considering "okay, claude works again, requeue the 22 jobs" is gated by the Post-Guard counter on `(slug, category=infra-cli-broken)`. It is set to 0 requeues by policy: infrastructure halts are operator-resolved, not auto-recovered. The 22 jobs stay in `3a-failed-pickup` until a human bulk-restores them.

**Why this is the right shape.** The agent's interpretation, "this looks like a broken CLI binary", is what we want from an LLM. It would have been almost impossible to encode every signature of "the CLI is silently broken" in deterministic code. But the *response* to that interpretation — halt the pipeline, do not auto-requeue, surface a banner — is policy that we want consistent across every diagnosis the LLM will ever produce. So the LLM does the part it is good at, and the code does the part the LLM is bad at.

## Relationship to other documents

- [AGENTS.md](../AGENTS.md) carries the rule-set summary that every agent run loads at startup. This file is the long-form derivation behind that summary.
- [ADR-0002](architecture-decisions.md) (deterministic orchestration over prompt trust) is the parent decision; this pattern is the next layer up.
- [ADR-0017](architecture-decisions.md) (supervisor advice-first) is the sibling that bounded *one* kind of agent (the supervisor) the same way; ADR-0032 generalises the rule.
- [docs/agent-task-contract.md](agent-task-contract.md) is the on-disk task lifecycle contract every CLI must respect. Different layer; the contract pattern is about *agent invocations*, the task contract is about *job folders*.
- [docs/loop-inventory.md](loop-inventory.md) is the registry that this pattern depends on.
- The marketing positioning of the pattern (as a product property, not an internal pattern) lives in `agent-studio-marketing/06-website-planung/deterministische-guardrails-um-agenten.md` in the marketing repository.
