# Claude outcome-classifier fixtures

Captured `claude` run transcripts, reduced to the consolidated `cli-output.log`
shape (one NDJSON object per line: `{"stream":"stdout|stderr|system","text":"..."}`).
Each file is one run that ended a specific way. `ClaudeOutcomeClassifierFixtureTests`
loads every file and drives it through `AgentOutcomeAnalyzer.Analyze` to lock the
verdict (`Done` / `Blocked` / `NeedsInput` / `NoOp` / `Unknown`) and the
`RunIssueKind`.

Why this folder exists: the broken-commit-pipeline incident (2026-06-08) slipped
through because the classifier matrix had unit coverage but no real-transcript
coverage for `claude`. A substantial claude reply that finished its work but did
not emit a parseable `[[TASK_DONE]]` was being classified `Unknown`, which spun
the reissue loop and left the work uncommitted. `done-substantial-no-sentinel`
and `substantial-neutral-no-verdict` are the executable guards for that exact
shape; the rest fill out the matrix so a future claude version bump that changes
a frame shows up as a fixture diff in one place.
