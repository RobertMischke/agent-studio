# Finding-first reissue prompt experiment

Version: finding-first-v1

Status: active controlled experiment; production default unchanged

## Motivation and evidence boundary

AGT-2322 found one narrow observational association in 162 mapped first
reissues. Specific-finding count had Spearman rho -0.273 with accepted attempt
and a bootstrap 95% interval from -0.420 to -0.117. Prompts with a finding line
averaged 1.83 attempts from the first mapped reissue, compared with 3.63 without
one. The composite sharpness index pointed in the opposite direction.

Those figures are observational evidence from
[`evidence-snapshot.json`](evidence-snapshot.json). They motivate this
experiment but do not identify a causal effect and do not authorize an
uncontrolled rollout.

## Arms and assignment

Eligible units are tasks entering a mapped automatic reissue with at least one
open finding. Assignment is 50/50 at the task level from the first eligible
reissue. SHA-256 over `finding-first-v1`, a newline, and the normalized
project/task key selects the arm. The first eight hash bytes are recorded as the
assignment hash. Every later eligible reissue for the same task remains in that
arm.

| Arm | Versioned template | Contract |
|---|---|---|
| Control | `runner-reissue-control-v1.md` | Current checklist-based reissue prompt. |
| Treatment | `runner-reissue-treatment-v1.md` | One numbered item per open finding with deficiency, known reference, required change, and focused verification. Raw follow-up evidence is in a separate evidence block. |

The treatment reorganizes the common finding payload. It copies each deficiency
verbatim, extracts a file, symbol, or artifact only when that reference appears
in the finding, and otherwise says that the source finding did not name one. It
does not invent a new deficiency. Both arms preserve the existing scope,
clarification, worktree, git, observability, and terminal-sentinel guardrails.

The experiment assignment does not select or override a coding model, thinking
level, review model, reviewer rubric, pipeline definition, or gate. Model and
thinking-level selection remains governed by
[`model-routing-policy.md`](../../system/domains/model-routing-policy.md),
including its correctness floors and reissue rule. The selected coding route is
recorded only to audit balance and drift.

## Hard telemetry

Each successfully started eligible run appends one row to
`logs/reissue-prompt-experiment.jsonl` with:

- experiment id, arm, template version, task-level assignment hash, and attempt;
- prompt family, typed quality-loop cause, and finding count;
- the coding model and thinking level already selected by normal routing.

Assignment and pipeline-attempt events are hard evidence. A spawn failure writes
no assignment row because no treatment was delivered. The analysis groups from
the first assignment and keeps later rows to audit arm and route consistency.

Causes come from the existing `quality_loop_reopened` timeline event. The
predeclared prompt families are `model-review-finding`, `deterministic-gate`,
`execution-protocol`, and `other-reissue`. The raw typed cause remains available
for narrower strata.

## Predeclared endpoints and analysis

The primary endpoint is attempts from the first eligible reissue through the
first acceptance. Acceptance is a model-judged orchestrator verdict, not a hard
quality fact. Attempt at first Grade A is the model-judged sensitivity endpoint.
The automatic code-review grade remains reporting evidence and never becomes a
lane gate.

Open tasks are right-censored at their last observed pipeline attempt. The arm
effect is treatment minus control restricted mean attempts at the common
observed horizon, estimated from Kaplan-Meier risk sets. Negative values favor
treatment. Uncertainty is a percentile 95% interval from 2,000 task-level
bootstrap resamples within arm.

The report includes:

- assigned, endpoint-observed, and right-censored counts per arm;
- the primary effect and Grade A sensitivity effect with uncertainty;
- prompt-family and raw-cause strata;
- coding-route counts and any within-task route drift;
- any within-task arm, template-version, or assignment-hash drift;
- deterministic-gate regression after the first assigned reissue.

Run:

```bash
node scripts/reissue-prompt-experiment-analysis.mjs \
  --root agent-taskboard-workspace/projects \
  --json docs/quality/pipeline-time-economy/reissue-prompt-experiment-analysis.json \
  --markdown docs/quality/pipeline-time-economy/reissue-prompt-experiment-analysis.md
```

The checked-in initial report has zero assignments because it predates
production exposure. It explicitly reports the resulting estimates as not
estimable instead of treating missing observations as zero.

## Production promotion gate

The production default remains unchanged unless all predeclared conditions hold:

1. at least 30 tasks are assigned to each arm;
2. no task shows arm, template-version, or assignment-hash drift;
3. treatment improves restricted mean attempts by at least 0.5 attempt;
4. the primary bootstrap interval is wholly below zero;
5. the upper bound for the treatment-minus-control deterministic-gate
   regression risk difference is at most 5 percentage points.

An arm comparison is experimental evidence. It does not make Grade A or
acceptance deterministic. If the gate is not met, the report recommendation is
to keep the production default unchanged.
