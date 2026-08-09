# Token Economy Task Server data Dossier

## Decision

Token Economy can reliably learn whether recorded system events happened, how
long recorded steps took, and how many tokens a producer reported. It cannot
infer absolute coding-model quality from the current task population.

Every proposed metric belongs to one of three evidence classes:

1. **Hard evidence:** deterministic or directly counted events such as gate
   verdicts, build failures, `agent-git-violation` events, timestamps, durations,
   token counters, attempt records, lane transitions, and Git SHAs. These are
   facts about the recorded run even without a performance baseline. Their
   coverage and measurement context must still be reported.
2. **Model-judged:** code-review grades, aspect verdicts, and LLM orchestrator
   decisions. These have no ground truth and an unknown reviewer bias. They are
   usable only as relative signals when the reviewed delivery, rubric, prompt,
   reviewer configuration, and evidence scope are held fixed.
3. **Confounded:** comparisons such as grade by coding model, cycles until Grade
   A, tokens per completed card, and runtime by coding model. Card difficulty,
   environment, review count, prompt quality, and pipeline configuration vary
   without control. The current production cohort cannot rank coding models.

## Proven extraction

The reproducible extractor in `extract-evidence.mjs` queried the stable Task
Server API on 2026-07-24. It deliberately selected the 60 most recently active,
non-archived Agent Studio cards carrying a code-review grade tag. This is a
technical coverage sample, not a representative performance cohort.
`extract-evidence.test.mjs` verifies the selection and aggregation contract
against an isolated mock Task Server.

Observed in the latest pipeline attempt per selected card:

- All 240 per-card API reads completed without an endpoint failure.
- 60 of 60 cards had a structured pipeline execution.
- 60 build/test gate records existed: 56 passed and 4 failed.
- 60 of 60 gate records had `durationMs`.
- 224 aspect-step records carried non-zero token counters.
- 0 of 60 task summaries carried a non-zero token total.
- 60 cards exposed a run timeline containing 426 run records, but 0 of 426
  records carried `durationSeconds`.
- 365 code-review records existed across the 60 cards.
- The selected coding-model mix was 39 `gpt-5.6-sol`, 20 `gpt-5.6-terra`, and
  1 `gpt-5.6-luna`. This imbalance is one explicit reason not to compare the
  observed grade distribution by coding model.

The sample is conditioned on having a grade tag and remaining outside the
archive. Its verdict counts cannot estimate the rate for all Agent Studio
tasks.

## Cheapest path to a real comparison

Start with one frozen, deterministic Token Economy benchmark scenario:

- same repository base SHA, prompt, attachments, permissions, CLI version,
  environment limits, gate commands, and reviewer setup;
- two coding models, randomized execution order, at least three repetitions;
- deterministic gate outcome as the primary endpoint;
- tokens and wall-clock as secondary endpoints with explicit coverage;
- grades and aspects reported separately as reviewer opinions.

This is the smallest direct coding-model comparison. Then add an inter-rater
replay of the same frozen deliveries using two reviewer models and report raw
agreement, a grade confusion matrix, and weighted Cohen's kappa. Repeat the
frozen scenario over time to measure stability. Public benchmark results are
context only unless model version, tool setup, task family, scoring rule, and
date are methodologically compatible.

## Source contracts

- `backend/Shared/Models/TaskInfo.cs`
- `backend/Shared/Models/TaskDetail.cs`
- `backend/Shared/Models/TaskTelemetry.cs`
- `backend/Features/Runner/RunTimeline.cs`
- `backend/Shared/Models/PipelineModels.cs`
- `backend/Features/Tasks/TaskPipelineEndpoints.cs`
- `backend/Features/Tasks/TaskRunnerEndpoints.cs`
- `backend/Features/Tasks/TaskCodeReviewEndpoints.cs`
- `docs/system/contracts/filesystem.md`

Source task: AGT-2293. Related benchmark context: AGT-2200.
