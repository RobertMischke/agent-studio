# Creative Design System - Concept Inventory

Status: design exploration. No naming committed.

## 1. What This Adds

The existing roadmap focuses on reliable task execution, security, quality checks, and review evidence. This mockup adds a generative design dimension plus explicit testing, QA, source-code, and token-usage perspectives:

| Concept | Scope | Trigger | Output | Produces work? |
|---|---|---|---|---|
| Design Loop | Task or project | Manual or after visual task | next version decision | Yes, via follow-up task |
| Screenshot Critique | Task | After screenshot capture | findings and suggestions | No, review evidence |
| Council Review | Task or project | Manual | role-separated critique | No, advisory |
| Visual Direction Skill | Project | Manual | design principles and references | Yes, design brief |
| UI Polish Skill | Task | Manual or review | concrete UI improvement plan | Yes, follow-up task |
| Copy Tone Skill | Task or project | Manual | improved product language | Yes, text suggestions |
| Brand Fit Review | Project | Manual | positioning notes | No, advisory |
| Design Reference Library | Project | Manual | screenshots, images, markdown briefs | No, context |
| Backend Test Run | Project or task | Button | pass/fail, logs, metrics | No, evidence |
| End-to-End Test Run | Project or task | Button | screenshots, trace, pass/fail | No, evidence |
| Tuning Test Run | Project or task | Button | latency, longtask, throughput | No, evidence |
| Coverage Report | Project or task | Button | coverage metrics and deltas | No, evidence |
| Source Map | Project | Button | modules, lines of code, dependencies | No, perspective |
| Module Organization Review | Project | Button | architecture and organization findings | No, advisory |
| Token Usage Heatmap | Project | Open project surface | spend distribution across jobs | No, perspective |
| Token Timeline | Project | Open project surface | spend over time | No, perspective |
| Token Drill-Down | Job or project | User click | category and run breakdown | No, evidence |

## 2. Clean Separation

| | Examines | Generates | Decides |
|---|---|---|---|
| Screenshot Critique | screenshots and current UI | findings | no |
| Council Review | screenshots, prompt, product context | role notes | no |
| Design Skill | product context and current UI | brief, variant prompt, copy | no |
| QA Skill | test output and metrics | report, findings | no |
| Source Analysis Skill | repository structure | map, metrics, organization findings | no |
| Token Usage Surface | job and run token records | charts, heatmap, outliers | no |
| Orchestrator | council notes and task state | next action | yes |
| Coding Agent | chosen task prompt | code changes | no lifecycle decision |

The council should never become another implementation actor. It observes and critiques. The orchestrator decides whether to accept, iterate, or queue follow-up work.

## 3. Recommended Vocabulary

- **Design Loop** - the full iteration cycle from intent to screenshot to critique to next version.
- **UX/UI** - project-level surface for design references, screenshots, council critique, design memory, and visual iteration actions.
- **Test Quality** - project-level surface for test history, QA runs, coverage, tuning results, source metrics, source maps, and module organization review.
- **Token Usage** - project-level surface for token totals, heatmaps, timelines, outliers, and drill-down.
- **Council** - a structured multi-role critique pass.
- **Version** - one visual attempt captured by screenshots and notes.
- **Design Evidence** - screenshots, variant notes, critique, chosen direction, rejected alternatives.
- **Design Skill** - reusable workflow that generates design guidance or critique.
- **Design Memory** - project-level record of accepted visual direction, brand notes, and example screens.
- **Design Reference** - screenshot, image, markdown note, external inspiration, accepted example, or rejected pattern.
- **QA Run** - explicit test action with structured metrics, logs, and artifacts.
- **Tuning Run** - performance or responsiveness test run with thresholds and measurements.
- **Source Map** - visual source-code perspective: modules, dependencies, lines of code, test coverage, and organization concerns.
- **Report Contract** - expected structured JSON block plus human Markdown report emitted by a Skill or script-backed action.
- **Job Tokens** - tokens used by the primary job run.
- **Supporting Jobs Tokens** - tokens used by attached analysis, design, security, QA, council, or source-map runs.
- **Orchestrator Tokens** - tokens used by orchestrator decisions, steering, reissues, summaries, and supervisor work related to the job.

Avoid using "Quality" as the umbrella. Design is not merely quality control. It is creative direction plus critique.

Avoid making these actions invisible. Design, QA, and source-analysis Skills should be user-triggered by buttons or explicit orchestrator steering. The app can recommend actions, but should not quietly run broad creative or testing loops unless a later project setting opts into a narrow, safe automation.

UX/UI, Test Quality, and Token Usage are project menu entries, not just Skill categories. A user looking at a project should be able to find all design evidence under UX/UI, all test/source-metric evidence under Test Quality, and all spend evidence under Token Usage without knowing which Skill generated it.

## 4. Council Roles

Recommended default council:

- Product: workflow usefulness, feature fit, clarity of user intent.
- Visual Design: hierarchy, layout balance, color, spacing, density, polish.
- Interaction Design: state model, controls, flow efficiency, feedback timing.
- Frontend Engineering: implementation feasibility, responsiveness, component reuse.
- Accessibility: contrast, focus order, semantics, touch targets, reduced motion.
- Marketing and Positioning: product story, tone, trust signal, memorable character.

Council output should be structured:

```json
{
  "version": "v2",
  "roles": [
    { "role": "Visual Design", "verdict": "warn", "notes": ["Primary action is visually weak."] }
  ],
  "orchestratorRecommendation": "request-next-version",
  "nextVersionPrompt": "Keep the dense taskboard layout, but make the review panel feel more intentional..."
}
```

## 5. Storage Shape

Runtime design evidence belongs in the job folder, not the app source repository.

Suggested shape:

```text
results/design/
  versions.jsonl
  council-v1.json
  council-v2.json
  references.jsonl
  briefs/
    visual-direction.md
    copy-tone.md
  screenshots/
    v1-desktop.png
    v1-mobile.png
    v2-desktop.png
    v2-mobile.png
```

Project-level design memory belongs in the watched project's documented project context or in a central app-managed design-memory file that points back to accepted evidence:

```text
.orchestrator/design-memory.md
```

That file should be human-readable and direct-CLI-friendly.

Testing and QA evidence should use a sibling shape:

```text
results/qa/
  runs.jsonl
  backend-tests-2026-05-04.md
  e2e-tests-2026-05-04.md
  tuning-2026-05-04.md
  coverage-2026-05-04.json
  traces/
  screenshots/
```

Source-code perspectives can be project-level or task-level:

```text
results/source-map/
  source-map.json
  module-organization.md
  metrics.json
```

These files are evidence generated by actions. They are not edited by hand as product documentation.

Token usage should be indexed across the project and link back to the job/run evidence:

```text
usage/tokens/
  project-summary.json
  timeline.json
  job-heatmap.json
  jobs/
    review-panel-polish.json
```

At minimum, each job token record needs:

- job id and title;
- state and project;
- job token total;
- supporting jobs token total;
- orchestrator token total;
- total tokens;
- run-level breakdown;
- timestamps for timeline placement;
- links to evidence or session logs when available.

## 6. Orchestrator Actions

The orchestrator needs explicit actions:

- Add design reference.
- Accept this version.
- Request next version.
- Ask council for harsher critique.
- Ask one role for deeper critique.
- Run backend tests.
- Run end-to-end tests.
- Run tuning tests.
- Generate coverage report.
- Generate source map.
- Analyze module organization.
- Open token heatmap.
- Inspect token outlier.
- Create follow-up implementation task.
- Save chosen direction to design memory.

These are steering actions, not hidden workflow branches.

## 7. Report Contract

Skills and script-backed actions should produce two layers:

1. Human Markdown report, optimized for review.
2. Structured JSON block, optimized for parsing.

Recommended envelope:

```json
{
  "kind": "source-map",
  "schemaVersion": 1,
  "status": "warn",
  "summary": "Frontend has three large components that should be split.",
  "metrics": {
    "files": 184,
    "linesOfCode": 27140,
    "testFiles": 43,
    "coverage": 76.2
  },
  "findings": [
    {
      "severity": "warn",
      "title": "Large component",
      "body": "orchestrator-side-sheet.component.ts is over the local size threshold.",
      "path": "frontend/src/app/components/orchestrator-side-sheet/orchestrator-side-sheet.component.ts"
    }
  ],
  "artifacts": ["results/source-map/source-map.json"]
}
```

If the JSON cannot be parsed:

- keep the Markdown visible;
- show "Unstructured report";
- expose raw output;
- allow the user to create a follow-up task manually.

The interface is expected output, not blind trust.

## 8. First Implementation Order

1. Add UX/UI, Test Quality, and Token Usage as project-level menu entries beside Security and Architecture.
2. Add design evidence contract to protocol docs.
3. Add design reference library and task/project reference picker.
4. Add screenshot version comparison in job detail.
5. Add local design Skill definitions.
6. Add QA run history for backend, end-to-end, tuning, and coverage actions.
7. Add source map action and code metrics panel under Test Quality.
8. Add Token Usage surface with category totals, heatmap, timeline, outliers, and drill-down.
9. Add council prompt and structured output parser.
10. Add "Next version" follow-up task action.
11. Add project-level design memory.

## Open Questions

1. Should Council be a project-level surface, a task detail action, or both?
2. Should the default council have six roles, or start with three roles: Product, Design, Engineering?
3. Should "next version" create a new queued task or continue the same task session?
4. How much design memory should be global to the app versus local to a watched project?
5. Should image-generation references be allowed in early implementation, or should the first cut use only screenshots from the running app?
6. Which QA actions are safe to suggest automatically, even if the user still triggers them?
7. What minimum report schema is stable enough for Skills across CLIs?
8. Should source metrics live on project pages, task details, or both?
9. Should the production label be "Test Quality", "QA", or "Testing and QA"? The mockup currently chooses "Test Quality" for the project menu because it includes test history plus code metrics.
10. How exact can token usage be across CLIs, and when must the UI mark values as estimates?
11. Should token heatmaps be project-wide, workspace-wide, or both?
