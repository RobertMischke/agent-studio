# Quality Studio analysis pipeline contract

Status: Consumer contract implemented; production package adapter blocked on QS-90 and QS-91 integration.

Quality Studio analysis is a standard pipeline bracket, not an optional external
tool. Agent Studio owns selection, task evidence, and retry policy. Quality Studio
owns analysis execution, rule content, rule identifiers, and its finding model.
The integration runs in the Agent Studio backend process through the
`AgentOrchestrator.CodeQuality` package. It does not call the Quality Studio HTTP
API.

## Dependency verification

The 2026-08-15 verification of the Quality Studio delivery refs found:

- QS-74 supplies the portable `QualityFindingEnvelope` with `ruleId`, stable
  fingerprint, locations, producer, and task-change subject.
- QS-88 is a URL preview and embed seam. It is relevant to the Quality Studio UI,
  not to server-side analysis execution.
- QS-90 defines the repository rule override at `.quality/rules.json`, the
  `quality-rules` sensor, and Angular rule ids `QS-NG-001` through `QS-NG-005`.
  Agent Studio references those ids but does not copy their statements, examples,
  or deterministic checks.
- QS-91 defines `QualityAnalysisCore` and package
  `AgentOrchestrator.CodeQuality` version `0.1.0`. Its delivery dossier marks the
  package unpublished. Its default core does not register QS-90's
  `quality-rules` sensor because the two delivery branches have not been
  integrated together.

Production activation therefore requires one integrated, published package in
which `quality-rules` is a named in-process analysis. Agent Studio must add an
exact package reference and a typed adapter to `IQualityStudioAnalysisCore` when
that artifact exists. Vendoring rule content, invoking a CLI, or falling back to
HTTP is not an accepted substitute.

## Step catalogue

The standard and UI iteration catalogues expose six first-class `Analysis`
steps. They run after deterministic build or UI preparation evidence and before
the ordinary aspect review.

| Step id | Axis | QS responsibility |
|---|---|---|
| `analysis-qs-static-rules` | Rule-based static pass | Resolve the QS rule library and emit deterministic findings. |
| `analysis-qs-model-review` | Model review | Emit findings through the QS finding model and provenance. |
| `analysis-qs-visual` | Visual and graphical quality | Review frontend-visible change evidence. |
| `analysis-qs-security` | Security | Combine security sensors and review evidence. |
| `analysis-qs-redundancy` | Redundancy | Find unnecessary duplicate implementation or structure. |
| `analysis-qs-consistency` | Consistency | Find cross-file or cross-component convention drift. |

The catalogue is not the activation policy. Every step remains visible so the
pipeline can record passed, failed, or not-applicable state without hiding axes.

## Convention policy

Selection is derived from the task's attributed changed paths.

| Card class | Default QS steps | Rule profile |
|---|---|---|
| Frontend touching | Static rules, visual | Angular |
| Backend touching | Static rules, security | .NET |
| Mixed frontend and backend | Static rules, visual, security | Angular and .NET |
| Other | Static rules | Core |

The static pass is the core default for every project. Projects may change named
step activation only through `.quality/agent-studio.json` committed in the
analyzed repository:

```json
{
  "schemaVersion": 1,
  "analysisSteps": {
    "analysis-qs-visual": false,
    "analysis-qs-consistency": true
  }
}
```

Unknown properties, schema versions, and step ids fail validation. Central
project settings and environment variables do not override this file. Rule
enablement and severity remain in QS-90's separate `.quality/rules.json`
contract, so Agent Studio does not become a second rule library.

## Findings, evidence, and retry

Each analysis writes
`results/quality-studio/<step-id>.json` under the task folder. The artifact
contains producer identity, producer version, card class, rule configuration
path, and the projected QS findings. Each finding is also appended to
`results/review-evidence.jsonl` with:

- source `quality-studio`;
- the exact QS `ruleId` in the title;
- stable finding or fingerprint identity;
- source locations and a link to the analysis artifact;
- description, recommendation, and evidence from the QS finding.

A non-security analysis with findings requests a steered retry. The retry input
must cite the named rule ids and file locations from evidence rather than copying
rule text into an Agent Studio prompt. Security findings are recorded and remain
visible, but unfixed security findings do not block the pipeline until this policy
is explicitly revised. This is aligned with the QS-90 rule-library direction.

## Activation gate

The consumer port, policy resolver, catalogue metadata, evidence writer, and
retry disposition are covered by Agent Studio tests. The post-core runtime must
not claim that the Angular pass ran until the integrated QS package is available.
Once available, the first production acceptance proof is a frontend card whose
attributed `.ts`, `.html`, `.scss`, or `.css` paths select the Angular profile,
run `analysis-qs-static-rules` in process, and leave a real QS finding artifact
or a zero-finding receipt in the task's collected `results/` directory.
