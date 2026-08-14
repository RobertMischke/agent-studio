# Quality Studio Analysis Pipeline Contract

Status: Accepted first slice, 2026-08-14

Quality Studio analysis is a standard Agent Studio pipeline bracket between the
deterministic build/test gate and model aspect review. Agent Studio selects and
routes analysis. Quality Studio owns rule definitions, matching, finding
identity, severity, recommendations, and sensor provenance.

## Catalogue

| Step id | Axis | Default card class | First-slice state |
|---|---|---|---|
| `post-qs-rule-analysis` | Rule-based static analysis | Angular or C# source changes | Implemented |
| `post-qs-model-review` | QS finding-model review | Frontend or backend | Reserved package-backed step |
| `post-qs-visual-quality` | Visual and graphical quality | Frontend | Reserved package-backed step |
| `post-qs-security` | Security | Backend | Reserved package-backed step |
| `post-qs-redundancy` | Redundancy | Frontend or backend | Reserved package-backed step |
| `post-qs-consistency` | Consistency | Frontend or backend | Reserved package-backed step |

The six rows use `StepKind.Analysis`. They are distinct from optional generic
tools and cannot be disabled through central `ProjectSettings` or environment
configuration.

## Convention-first policy

The built-in policy applies automatically to every registered project:

- Frontend-touching cards run the named rule pass, model review, visual axis,
  redundancy axis, and consistency axis. The rule package resolves the Angular
  rules `QS-NG-001` through `QS-NG-005` from the QS-90 library.
- Backend-touching cards run the named rule pass, model review, security axis,
  redundancy axis, and consistency axis. The rule package resolves the C# rules
  `QS-CS-001` through `QS-CS-004` from the QS-90 library.
- Cards without Angular or C# source classification record the rule pass as
  not applicable.

Agent Studio references these ids but does not copy their rule text. Quality
Studio remains the rule system of record.

One versioned file in the reviewed repository is the only override:
`.quality/agent-studio-pipeline.json`. It follows
[`quality-analysis-policy.v1.schema.json`](../../app/schemas/quality-analysis-policy.v1.schema.json).
For example:

```json
{
  "$schema": "https://agent-taskboard.local/schemas/quality-analysis-policy.v1.schema.json",
  "schemaVersion": 1,
  "steps": {
    "post-qs-visual-quality": { "enabled": false }
  }
}
```

There is no environment-specific, workspace, or central project-setting
override. Quality Studio continues to read its own repository rule selection
from `.quality/rules.json`.

## In-process boundary

Server-side steps reference `AgentOrchestrator.CodeQuality` and call
`QualityAnalysisCore.RunAsync(QualityAnalysisRequest)`. Each unit is a
`NamedQualityAnalysis`; the first slice selects the `quality-rules` sensor with
path scope and `PersistArtifacts: false`. No Agent Studio pipeline request uses
the Quality Studio HTTP API. A future Quality Studio UI may receive findings
asynchronously without changing this execution boundary.

## Evidence and retry

Every rule pass writes
`results/quality-studio/post-qs-rule-analysis.v1.json` under the card folder.
The artifact follows
[`quality-studio-analysis-evidence.v1.schema.json`](../../app/schemas/quality-studio-analysis-evidence.v1.schema.json)
and names analyzed paths, applied QS rule ids, findings, package version, source
commits, and sensor provenance.

Each finding is also appended to `results/review-evidence.jsonl` with its named
rule id, fingerprint, file location, recommendation, and artifact reference.
Medium, high, and critical rule findings fail the analysis step and enter the
bounded quality-loop reissue path. The follow-up and its steering context are
persisted so the next run receives the exact named findings. Package or policy
failure uses the same bounded path instead of silently passing.

Security is the deliberate exception for the current policy. Security findings
remain in the analysis artifact and review evidence but never block or reopen a
card. This records the QS-90 policy while preserving a future explicit decision
about enforcement.
