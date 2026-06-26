# CLI Domain Map

Version: 2026-06-09
Status: System-of-record map for CLI adapter and quota changes.

Use this when a change touches Claude, Codex, Copilot, Gemini, prompt handoff,
stream parsing, session capture, quota probes, model catalogs, sandbox modes, or
CLI execution tests.

## Entry Points

- [docs/cli/supported-clis.md](../cli/supported-clis.md) defines the cross-CLI invocation
  contract.
- [docs/cli/skills/cli-overview.md](../cli/skills/cli-overview.md) covers adapter
  invariants.
- Per-CLI deep refs:
  [Claude](../cli/skills/cli-claude.md),
  [Codex](../cli/skills/cli-codex.md),
  [Copilot](../cli/skills/cli-copilot.md),
  [Gemini](../cli/skills/cli-gemini.md).
- [docs/cli/skills/sandbox-and-yolo.md](../cli/skills/sandbox-and-yolo.md) covers
  permission, sandbox, and effective-mode behavior.
- [docs/cli/audits/startup-cost-analysis-2026-05-09.md](../cli/audits/startup-cost-analysis-2026-05-09.md)
  is the spawn/probe/discovery cost analysis.
- [docs/cli/investigations/codex-runner-investigation.md](../cli/investigations/codex-runner-investigation.md) records
  the Codex stdin-via-`-` incident and regression guard.

## Key Code

- `backend/Services/Cli/`: CLI drivers and shared execution base.
- `backend/Services/Cli/CliRouter.cs`: `cliType` routing.
- `backend/Services/Quota/*QuotaProbe.cs`: per-CLI quota probes.
- `backend/Services/Quota/QuotaService.cs`: aggregate quota surface.
- `backend/Endpoints/CliEndpoints.cs`: sessions, versions, quota, and model
  endpoints.
- `backend/Services/Runner/OrchestratorSession.cs` and
  `OrchestratorRunner.cs`: runner-to-CLI orchestration boundary.
- `prompts/runtime/`: prompt templates handed to the CLIs.
- `frontend/src/app/features/cli/`, `frontend/src/app/features/tokens/`, and
  `frontend/src/app/components/cli-model-selector/`: CLI status, usage, quota,
  and model UI.

## Invariants

- Every driver must satisfy the same contract: start process, stream output,
  capture session identity when available, report completion, surface quota and
  permission issues, and preserve terminal sentinels.
- CLI skills are required reading before changing the matching driver.
- Prompt-template edits are behavior changes. String-render tests are not enough
  because the adapter can still hand a bad shape to the live CLI.
- Sandbox and permission behavior must be explicit per CLI. Do not hide a
  permission block behind a generic failure.
- Quota probes are observability surfaces. Preserve stable event names and
  useful error context when editing nearby code.

## Verification

- Driver changes need focused unit tests for frame parsing, session capture,
  error classification, and command construction.
- Prompt or execution-path changes need the matching live probe, such as
  `claude-hello-world.spec.ts` or the equivalent for the affected CLI.
- Quota/model UI changes need frontend tests plus Playwright when behavior or
  rendering changes.
- For Codex changes, re-check current CLI behavior before relying on older
  recovery heuristics.
