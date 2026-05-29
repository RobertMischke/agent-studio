# Spec / Task / Job Drift

You are running the **Spec / Task / Job Drift** producer for project `{{project}}`.

Your job is to compare the project's specifications, planning docs, queued and
in-flight task prompts, completed evidence, and prior analysis reports, and
answer:

> Are the queued and active tasks still aligned with stated intent? Are any
> two tasks duplicates or contradictions? Has any completed evidence
> contradicted task acceptance? Are any tasks stale, pointing at old product
> direction? Are any task prompts missing enough context to execute safely?

This is **read-only analysis**. Do not edit any task prompt, do not move any
job between lanes, and do not relax the "one active coding task per project"
boundary. The only artifacts you may produce are this report's Markdown body
and one fenced JSON sidecar at the end (see "Output contract" below).

## Scope

- Project: `{{project}}`
- Captured at (UTC): `{{captured_at}}`
- Repository root: `{{repo_root}}`
- Project root (job folders): `{{project_root}}`

## Specifications and planning documents

Read these first. They are the project's stated intent. Cite them by relative
path in your findings.

{{spec_docs}}

## Active queue (1-preparation through 5-human-review)

These are the jobs the project is committing to or already working on. Each
entry is the lane folder, the job slug, the title, and a short excerpt from
`prompt.md` so you can spot duplicates, contradictions, or thin context
without re-reading every prompt. `status.md` and `logs/` markers tell you
which jobs already have run evidence you can drill into.

{{active_jobs}}

## Recent completed evidence (`6-completed`)

Use these to detect:

- A queued task whose work was already shipped under a different slug.
- A completed task whose evidence (status.md / logs / commits) contradicts
  the acceptance promised in its prompt.

{{recent_completed}}

## Duplicate candidates (heuristic)

The host computed a token-overlap heuristic on slug + title. The pairs below
*may* be duplicates; confirm or dismiss each one against the prompts before
calling them out in your verdict.

{{duplicate_candidates}}

## Recent drift reports

If a recent drift report already covered a similar finding, build on it
rather than re-deriving the same point.

{{recent_drift_reports}}

## Recent analysis reports

Adjacent inspection evidence that may overlap with task / job drift.

{{recent_analysis_reports}}

## What you must produce

A single Markdown reply structured like this:

1. **Verdict** - one sentence: healthy, watch, warn, or critical, plus the
   load-bearing reason in plain words.
2. **Duplicate or contradictory tasks** - bullet list of pairs you confirm
   are duplicates or that contradict each other. Cite both job slugs.
3. **Spec drift on the active queue** - tasks that no longer match current
   roadmap intent or that reference old product direction.
4. **Acceptance contradictions** - completed evidence that contradicts what
   the task prompt promised. Cite the completed-job slug and the file in
   `status.md` / `logs/` that shows the mismatch.
5. **Thin or unsafe prompts** - active jobs whose `prompt.md` is too sparse
   or ambiguous to execute safely. State what context is missing.
6. **Follow-up task suggestions** - candidate jobs to queue for cleanup,
   merging, splitting, documentation, or rewording. Suggestions are drafts;
   the user creates the actual job.
7. **Evidence pointers** - relative doc paths, job slugs, and prior report
   ids you relied on.

If a finding cannot be cited to a specific path, slug, or report id, leave
it out. Confidence without evidence is worse than an explicit "not enough
source coverage to score this dimension".

## Output contract

Append exactly one fenced JSON block at the very end of your reply. The
Markdown body above is the durable human artifact; the JSON sidecar is the
additive app contract. If you cannot produce valid JSON, omit the block
entirely - an unstructured report is still a useful report.

The schema's full enum is wider, but for this producer prefer the dimensions
called out in the task contract:

- **Spec** - written specifications vs the active queue.
- **TaskJob** - queued / active / completed jobs vs the prompts and the
  evidence on disk.
- **Intent** - cases where the queue is heading away from stated product
  goals (use sparingly; intent drift is broader than queue churn).
- **Process** - cases where the queue itself violates a process rule
  (parallel coding tasks, missing acceptance, etc.).

```json
{
  "verdict": "<one-sentence summary of the drift state>",
  "scoreBand": "Healthy|Watch|Warn|Critical|Unknown",
  "overallScore": 0,
  "dimensions": [
    {
      "type": "Spec|TaskJob|Intent|Process",
      "score": 0,
      "severity": "Info|Warn|High|Critical",
      "confidence": 0.0,
      "sourceCoverage": 0.0,
      "status": "New|Accepted|Ignored|Tracked|Resolved",
      "summary": "<one-line description>",
      "evidenceRefs": ["<doc path, job slug, or prior report id>"],
      "recommendedActions": ["<short imperative action>"]
    }
  ],
  "followUpTaskSuggestions": [
    {
      "title": "<short imperative title>",
      "summary": "<2-3 sentence rationale>",
      "priority": "Low|Normal|High|Critical",
      "relatedDimension": "Spec|TaskJob|Intent|Process"
    }
  ]
}
```

If you found no drift at all, omit the `dimensions` array entirely (or set it
to `[]`); the host will synthesise a single Healthy snapshot so the record is
still schema-valid. Set `scoreBand` to `Healthy` and `overallScore` to a
value at or above 80.

## Hard constraints

- Do not edit any `prompt.md`, `status.md`, `job.json`, or log file.
- Do not move jobs between lanes; the runner is the single state-machine
  authority.
- Do not relax the "one active coding task per project" rule when proposing
  follow-ups; cleanup work goes through the normal queue.
- Cite evidence by stable id (doc path, job slug, prior report id). Do not
  copy raw log bodies into the report.
- Do not treat the score itself as a decision. The score is triage; the
  decisions stay with the user.

End your reply with `[[TASK_DONE]]` on its own line so the runner records
the analysis as completed.
