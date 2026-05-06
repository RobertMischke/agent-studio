# Runtime log analysis - helper scripts

Companion scripts for the [`runtime-log-analysis`](../SKILL.md) skill. The
skill renders the report; the scripts pre-compute the deterministic
aggregates so two different CLIs produce comparable findings from the same
input.

## `aggregate-runtime-events.mjs`

Reads one or more `ProductRuntimeEvent` JSONL files
([schema](../../../../docs/schemas/product-runtime-event.schema.json)) and
prints a JSON aggregate covering the six finding categories the skill
turns into a report.

### Usage

```bash
node aggregate-runtime-events.mjs <jsonl> [more.jsonl ...]
```

- Reads UTF-8 text. Skips empty lines. Captures malformed lines into a
  `parseWarnings[]` array on stdout instead of failing the whole run.
- Never writes to disk. Never opens a network. Read-only by construction.
- Exits `0` on success, `2` on usage error, `3` when no input lines were
  readable.

### Output shape

```jsonc
{
  "schemaVersion": 1,
  "inputCount": 142,
  "fileCount": 1,
  "window": { "from": "2026-05-06T12:00:00.000Z", "to": "2026-05-06T12:01:55.000Z" },
  "repeatedErrors":         [ /* groups */ ],
  "slowOperations":         [ /* p50/p95/p99 per (subsystem, operation) */ ],
  "noisyEvents":            [ /* top-share or rate-spike events */ ],
  "missingCorrelationIds":  [ /* per-subsystem null-streaks */ ],
  "suspiciousSequences":    { "violations": [], "unverified": [] },
  "parseWarnings":          [ /* line-level diagnostics */ ]
}
```

The skill turns this object into the Markdown skeleton documented in
[`../references/report-contract.md`](../references/report-contract.md).
The shape is intentionally narrow: every field maps to a finding category
or to data quality. Do not bolt on findings the contract does not name.

### Constraints inherited from the skill

- **Read-only.** The script never opens a file for writing and never calls
  `fs.writeFileSync`.
- **No network.** No `fetch`, no `node:http`. Disconnected hosts must run
  it identically.
- **No automation.** The script is a tool the skill calls during a
  user-triggered run; nothing in the orchestrator schedules it.

### Tests

Locked by [`backend.Tests/RuntimeLogAnalysisSkillTests.cs`](../../../../backend.Tests/RuntimeLogAnalysisSkillTests.cs):

- `Aggregator_OnSampleJsonl_ProducesExpectedFindingShape` runs the script
  against `tests/fixtures/sample-runtime.jsonl` and asserts the aggregate
  contains the six top-level keys plus a non-empty
  `repeatedErrors` group.
- `Aggregator_OnMixedJsonl_PreservesParseWarnings` asserts malformed lines
  end up in `parseWarnings[]` rather than aborting the run.
- `SkillFile_HasFrontmatterSentinelAndRequiredSections` locks the SKILL.md
  shape so a future refactor does not silently break the report contract.
- `SampleReport_ValidatesAgainstAnalysisReportSchema` asserts the example
  report fixture round-trips through `AnalysisReportStore`.

Run them with `dotnet test backend.Tests --filter RuntimeLogAnalysisSkillTests`.

### Project-specific invariants

`detectSuspiciousSequences()` ships a small default invariant list. A
project that wants to extend it should fork the array near the top of
`aggregate-runtime-events.mjs`; the skill flags **unverified** orderings as
`unverified-sequence` so reviewers can decide which ones become hard
invariants for the project.
