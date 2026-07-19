# Mockup: Task-Detail Pipeline Timeline

Task-detail surface (read/observe). Shows the planned step sequence up front, live progress on the executing step, and per-step status + duration + attempt + artifact + orchestrator verdict. Renders on the `timeline.jsonl` ledger ([ADR-0049](../../system/architecture/decisions/adr-archive.md#adr-0049)). Spec: [ADR-0051 section 5.2](../../system/architecture/decisions/proposed/adr-0051-task-processing-pipeline.md#52-task-detail-level-the-timeline-readobserve).

## Mid-run state (core done, build running, rest planned)

The whole sequence is visible from the start. Done steps are solid; the running step pulses with live elapsed/tokens; later steps are greyed/pending.

```
+--------------------------------------------------------------------------------------+
|  ATP-141  Add pipeline editor             Run #2        Pipeline v3      [ Artifacts ]|
+--------------------------------------------------------------------------------------+
|                                                                                      |
|  PRE                                                                                  |
|   (o)  done   Context retrieval            llm      1.2s    pass        [ artifact ]  |
|    |                                                                                  |
|  CORE                                                                                 |
|   (o)  done   Agent execution              llm     4m 18s   done        [ log ]      |
|    |                                                                                  |
|  POST                                                                                 |
|   (>)  RUN    Build (dotnet build)      script   00:23 ...  HARD-GATE   [ live log ] |
|    |          $ dotnet build   |  streaming stdout...                                 |
|    |                                                                                  |
|   ( )  wait   Requirement fit              llm        --     review     (pending)     |
|   ( )  wait   Code quality                 llm        --     review     (pending)     |
|   ( )  wait   Documentation impact         llm        --     review     (pending)     |
|   ( )  wait   Tests & evidence             llm        --     review     (pending)     |
|   ( )  wait   Frontend stylelint        script        --    scripted    (pending)     |
|   ( )  wait   Auto-review decision         llm        --     review     (pending)     |
|                                                                                      |
+--------------------------------------------------------------------------------------+

(o) ok   (>) running (pulse)   ( ) pending   (x) failed   (!) warn   (-) skipped
```

## Failed hard-gate state (the 0aa9242 class)

Build fails; because it is `failureMode: hard, reaction: scripted`, the pipeline stops deterministically and the downstream steps are skipped. No LLM judgement, no push.

```
+--------------------------------------------------------------------------------------+
|  ATP-141  Add pipeline editor             Run #2        Pipeline v3      [ Artifacts ]|
+--------------------------------------------------------------------------------------+
|  POST                                                                                 |
|   (x)  FAIL   Build (dotnet build)      script    11.4s    HARD-GATE   [ artifact ]  |
|    |          exit 1  -  CS0103: 'EnsureUniqueSlug' does not exist                    |
|    |          >> pipeline halted: hard gate failed. Core work reissued (attempt 3).   |
|   (-)  skip   Requirement fit              llm        --              (skipped)       |
|   (-)  skip   Code quality                 llm        --              (skipped)       |
|   (-)  skip   ... (remaining post-steps skipped)                                      |
+--------------------------------------------------------------------------------------+
```

## Orchestrator-review state (soft LLM step with a reopen verdict)

A `review` step's artifact is read by the orchestrator (ADR-0032: agent classifies, rule engine decides). Verdict shown inline; reopen ticks the completion-loop (ASS-566).

```
|   (!)  warn   Code quality                 llm     6.1s    review      [ artifact ]  |
|    |          verdict: concerns -> reopen                                             |
|    |          "Magic numbers in PipelineExecutor; extract to named constants."        |
|    |          >> orchestrator reissued core work with this feedback (attempt 3 / 5).  |
```

## Per-step artifact (opened)

Every step produces a markdown artifact. Script artifact = captured output + verdict header; LLM artifact = model output.

```
+--------------------------------------------------------------------------------------+
|  Artifact: Build (dotnet build)  -  attempt 2  -  2026-05-30T14:22:09Z      [ x ]    |
+--------------------------------------------------------------------------------------+
|  verdict: fail   exitCode: 1   durationMs: 11402   reaction: scripted                 |
|  ------------------------------------------------------------------------------------ |
|  $ dotnet build                                                                       |
|  ...                                                                                  |
|  backend/Services/Jobs/JobMutationService.cs(212,17): error CS0103: The name          |
|     'EnsureUniqueSlug' does not exist in the current context                          |
|  Build FAILED.                                                                        |
+--------------------------------------------------------------------------------------+
```

## Step-history drill-down (links to the project analytics view)

Each step row has a hover affordance into the cross-task CI/CD-style stats ([ADR-0051 section 6.4](../../system/architecture/decisions/proposed/adr-0051-task-processing-pipeline.md#64-aggregates--queries)), served from the derived `pipeline-history.db`.

```
  Build (dotnet build)   runs 142   p95 12.0s   fail-rate 4.2%   trend v  [ See history ]
```

## Notes (for the Slice 1 / Slice 3 implementation)

- The full sequence renders from the **definition** (planned), overlaid with `step_runs` events from `timeline.jsonl` (actual). A step with no event yet renders pending/greyed.
- LLM steps stream elapsed + tokens while running (Slice 3), the same capture as the core run.
- Script steps stream captured stdout into the live-log popover (Slice 1).
- Verdict pills reuse the aspect verdict vocabulary (`pass` / `concerns` / `block`) already on `PipelineStepExecution.Verdict` (ADR-0045).
