# Mockup: Project Pipeline Editor

Project-level surface. Defines the ordered pre- and post-steps for one project. Reached from the project Settings panel (a new "Pipeline" section). Spec: [ADR-0051 section 5.1](../../adr/adr-0051-task-processing-pipeline.md#51-project-level-the-pipeline-editor).

## Default state (the seeded `standard-task-pipeline`, version 1)

```
+--------------------------------------------------------------------------------------+
|  Project: agent-taskboard            Pipeline  v3 (current)   [ History v ] [ + Step ]|
+--------------------------------------------------------------------------------------+
|                                                                                      |
|  PRE-PROCESSING                                                  (runs before agent)  |
|  +--------------------------------------------------------------------------------+  |
|  |  (empty - drag a step here or click + Step)                                     |  |
|  +--------------------------------------------------------------------------------+  |
|                                                                                      |
|  CORE                                                                  (not editable) |
|  +--------------------------------------------------------------------------------+  |
|  | [::]  Agent execution                          llm    core      Claude Opus 4.8 |  |
|  +--------------------------------------------------------------------------------+  |
|                                                                                      |
|  POST-PROCESSING                                                 (runs after agent)   |
|  +--------------------------------------------------------------------------------+  |
|  | [::]  Build (dotnet build)        script   HARD-GATE  scripted        exit-code |  |
|  |       |                                                                         |  |
|  | [::]  Requirement fit              llm        soft     review     Sonnet 4.6    |  |
|  | [::]  Code quality                 llm        soft     review     Sonnet 4.6    |  |
|  | [::]  Documentation impact         llm        soft     review     Sonnet 4.6    |  |
|  | [::]  Tests & evidence             llm        soft     review     Sonnet 4.6    |  |
|  |       |  (the four aspects run in parallel)                                     |  |
|  | [::]  Frontend stylelint          script      soft    scripted       exit-code  |  |
|  | [::]  Auto-review decision         llm        soft     review     Opus 4.8      |  |
|  +--------------------------------------------------------------------------------+  |
|                                                                                      |
|  [ Describe a pipeline in words... (AI-assist) ]                          [ Save v4 ]|
+--------------------------------------------------------------------------------------+

[::]  = drag handle (drag to reorder within a phase)
HARD-GATE = failureMode: hard (red accent). soft = warn-only (muted).
```

## Per-step config (expanded row)

Clicking a step row expands its config inline. Script step shown:

```
+--------------------------------------------------------------------------------------+
| [::]  Build                                                              [ x Remove ] |
|  +--------------------------------------------------------------------------------+  |
|  |  Label      [ Build                                       ]                     |  |
|  |  Type       ( ) LLM        (o) Script                                           |  |
|  |  Command    [ dotnet build                                ]   Timeout [ 300 ]s  |  |
|  |  Failure    (o) Hard (fail the pipeline)   ( ) Soft (warn only)                 |  |
|  |  Reaction   ( ) Review (orchestrator reads artifact)  (o) Scripted (exit-code)  |  |
|  |  Enabled    [x]                                                                 |  |
|  +--------------------------------------------------------------------------------+  |
+--------------------------------------------------------------------------------------+
```

LLM step shown (Reaction defaults to Review; Model picker is the shared selector):

```
+--------------------------------------------------------------------------------------+
| [::]  Code quality                                                      [ x Remove ] |
|  +--------------------------------------------------------------------------------+  |
|  |  Label      [ Code quality                                ]                     |  |
|  |  Type       (o) LLM        ( ) Script                                           |  |
|  |  Model      [ Claude Sonnet 4.6      v ]   <- shared <app-cli-model-selector>   |  |
|  |  Prompt     [ Review the diff for code-quality regressions against the      ]  |  |
|  |             [ task's stated intent. Output pass | concerns | block.         ]  |  |
|  |  Failure    ( ) Hard       (o) Soft                                            |  |
|  |  Reaction   (o) Review     ( ) Scripted                                        |  |
|  |  Enabled    [x]                                                                 |  |
|  +--------------------------------------------------------------------------------+  |
+--------------------------------------------------------------------------------------+
```

## AI-assist drawer

Operator types a description; the assistant proposes a draft definition the operator confirms (never auto-applied). The proposal is a schema-validated draft ([ADR-0032](../../architecture-decisions.md#adr-0032)).

```
+--------------------------------------------------------------------------------------+
|  AI-assist                                                                  [ x ]    |
|  +--------------------------------------------------------------------------------+  |
|  | "lint scss, build the backend as a hard gate, run smoke tests, then have the   |  |
|  |  orchestrator review the diff against the prompt's intent"                      |  |
|  |                                                                  [ Propose -> ] |  |
|  +--------------------------------------------------------------------------------+  |
|                                                                                      |
|  Proposed POST steps (review before applying):                                       |
|    1. Frontend stylelint     script   soft   scripted     npm run lint:scss          |
|    2. Build (backend)        script   HARD   scripted     dotnet build               |
|    3. Smoke tests            script   HARD   review       dotnet test --filter Smoke |
|    4. Diff intent review     llm      soft   review       Sonnet 4.6                 |
|                                                                                      |
|                                          [ Discard ]   [ Apply as draft v4 ]         |
+--------------------------------------------------------------------------------------+
```

## Version history menu (text-only)

```
  History v
  +---------------------------------------------+
  |  v3  current   2026-05-30  robert  8 steps  |
  |  v2            2026-05-22  robert  7 steps  |
  |  v1  seeded    2026-05-29  system  7 steps  |
  |  ------------------------------------------ |
  |  Compare v2 -> v3                            |
  |  Revert to v2                                |
  +---------------------------------------------+
```

## Interaction notes (for the Slice 4 implementation)

- Drag a `[::]` handle to reorder within a phase; rewrites `order` and optimistically re-renders, PUT fire-and-forget, rollback-on-error toast (ADR-0046).
- `+ Step` appends a disabled draft step to the focused phase.
- Saving writes a **new version** of the definition under `.metadata/pipelines/<projectId>/<version>.json` and advances `current.json`.
- The CORE row is read-only (the agent run; ADR-0045). Pre/post are editable.
