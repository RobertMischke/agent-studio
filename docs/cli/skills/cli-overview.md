---
name: cli-overview
description: Cross-cutting reference for working on the four CLI integrations in agent-taskboard. Use this skill alongside the per-CLI skills (cli-claude / cli-codex / cli-copilot / cli-gemini) whenever you touch backend/Services/Cli/*, backend/Services/CopilotCliService.cs, backend/Services/Quota/*, the activity-log parser, or anything that consumes CLI output. Contains: the contract every driver must satisfy, where each piece of state lives on disk, the seven invariants we keep tripping over, and an index of the per-CLI skills.
sentinel: TASKBOARD-CLI-SKILL-OVERVIEW-2026
---

<!-- SENTINEL: TASKBOARD-CLI-SKILL-OVERVIEW-2026 — pickup-tests assert any CLI driving the repo can echo this back. -->

# CLI integrations: cross-cutting reference

This project drives **four** coding-agent CLIs from a single backend: Claude Code, OpenAI Codex, GitHub Copilot, and Google Gemini. They share a common contract but behave very differently in practice. This skill captures the cross-cutting things that are easy to get wrong; the per-CLI skills (`cli-claude`, `cli-codex`, `cli-copilot`, `cli-gemini`) carry the CLI-specific operational knowledge.

> **Authoritative contract:** [docs/cli/supported-clis.md](../supported-clis.md) is the formal "what every supported CLI must provide" document. It is updated in the same PR that touches a CLI integration. This skill complements it with operational/working knowledge.

## When to use which skill

| You are working on … | Read first |
|---|---|
| `backend/Services/Cli/ClaudeCliService.cs`, Claude `stream-json` framing, Claude session capture, Claude rate-limit pill | `cli-claude` |
| `backend/Services/Cli/CodexCliService.cs`, Codex `--json`, Codex session_meta, `codex exec` | `cli-codex` |
| `backend/Services/CopilotCliService.cs` (legacy code path), `--allow-all`, `gh auth token` integration, named-slug sessions | `cli-copilot` |
| `backend/Services/Cli/GeminiCliService.cs`, Gemini `init` frame, `--skip-trust` / `-y`, the buffered-stdout limitation | `cli-gemini` |
| Adding a new CLI | This skill + [docs/cli/supported-clis.md](../supported-clis.md) §4 checklist |
| Activity-log marker parser, conversation rendering | This skill (§ "Marker-line vocabulary") + the per-CLI `TransformReadLine` notes |
| Resume / continuation logic across CLIs | This skill (§ "Session model invariants") + `cli-claude` (UUID), `cli-copilot` (slug) |

## Architecture at a glance

```text
                               ┌──────────────────────────────┐
                               │   ProjectRunner.RunCliAsync  │  RunIntent: ManualStart / AutoPickup / UserContinue
                               └────────────┬─────────────────┘
                                            │ RunPlanner.PlanRun
                            ┌───────────────┴──────────────┐
                            │ RunPlan { template, resume, } │
                            └───────────────┬──────────────┘
                                            │
                                            ▼
                                   CliRouter.Get(cliType)
                                            │
        ┌─────────────────┬─────────────────┼─────────────────┬───────────────┐
        ▼                 ▼                 ▼                 ▼               ▼
ClaudeCliService    CodexCliService   GeminiCliService    CopilotCliService    (new CLI here)
   (base class)       (base class)     (base class)      (legacy code path)
        │                 │                 │                 │
        └────┬────────────┴────────┬────────┘                 │
             │                     │                          │
             ▼                     ▼                          ▼
   stream-json frames        plain text                   plain text
        │                       │                            │
        └───── TransformReadLine() → marker lines ───────────┘
                                            │
                                            ▼
                                   logs/cli-output.log  (JSONL, source of truth)
                                            │
                                            ▼
                              frontend activity-log.parser
                                            │
                              ┌─────────────┴────────────┐
                              ▼                          ▼
                      Conversation mode             Trace mode
                      (joined turns,              (per-group
                       Markdown rendered)         chronological)
```

Three of the four drivers (`Claude`, `Codex`, `Gemini`) extend [`CliExecutionServiceBase`](../../../backend/Services/Cli/CliExecutionServiceBase.cs). The fourth, `CopilotCliService`, predates the base class and reimplements lifecycle; do not refactor it onto the base class as a side quest, but copy patterns *into* it when fixing bugs.

## Session model invariants

Each CLI has its own session id format. The two big classes are **UUID** (Claude / Codex / Gemini) and **slug** (Copilot).

| CLI | Format | How we capture it | Resume flag |
|---|---|---|---|
| Claude | `8-4-4-4-12` UUID | Parse first `system` `stream-json` frame in `OnOutputLine`; written to a marker line `● Session init <uuid>` then read back. | `-r <uuid>` |
| Codex | UUID | Parse `thread_id` of the first `thread.started` JSON line (legacy `session_meta.payload.id` also accepted) in `MapLineToRunEvents` — the **raw**-line hook, because `TransformReadLine` now rewrites it to `● Session <id>`. | `exec resume <uuid> "<prompt>"` (positional, before `--json`) |
| Gemini | UUID | The `init` stream-json frame is rendered to `● Session init <uuid> (<model>)` by `TransformReadLine`; `OnOutputLine` runs on the **transformed** line and reads the UUID back via regex. | `-r <uuid>` (also accepts numeric index or `latest`, but we persist UUIDs only) |
| Copilot | Slug (`taskboard-<jobId>-YYYYMMDDHHmm`) | Pre-generated by `RunPlanner.BuildSessionName` before the run; passed via `--name=` on first run, `--resume=` thereafter. | `--resume="<slug>"` |

**Hard invariant:** `IsCompatibleSessionName` must reject every other CLI's id. Accepting an alien id leads to silent hangs (Claude's `-r` waits forever on a slug, Codex's `exec resume` errors out, Copilot starts a fresh session and the resume is lost). The unit tests in `backend.Tests/TaskRunnerPlanTests.cs` lock this matrix; do not relax them without first reading the regression history they protect against.

**Two capture strategies, both valid:** the base class invokes `OnOutputLine` *after* `TransformReadLine` but `MapLineToRunEvents` *before* it.
- **Claude / Gemini** capture in `OnOutputLine` by reading the rendered `● Session init <uuid>` marker back via a narrow regex. This works because the renderer emits the UUID verbatim into the marker.
- **Codex** captures in `MapLineToRunEvents` by `JsonDocument.Parse`-ing the **raw** `thread.started` frame. It must use the raw-line hook because its renderer collapses the frame to `● Session <id>` with no other recoverable fields, and because the raw hook is where `TryCaptureTurnUsage` already lives.

Either is fine; what breaks is mixing them — reading the marker in `OnOutputLine` *and* having a `TransformReadLine` that emits an extra line which also looks like the session marker double-captures. Keep capture regexes narrow, and when a renderer rewrites the session frame, capture from the raw line instead.

### Stale-session invariants

A stale session is not the same thing as a missing session. Missing sessions reject the resume target and route through Recovery. Stale sessions still resume, but the agent may have lost useful context, older reasoning, cached prompt state, or alignment with the job's current disk evidence.

The [April 23, 2026 Anthropic Claude Code postmortem](https://www.anthropic.com/engineering/april-23-postmortem) is the cautionary example: a product-layer optimization for sessions idle over one hour accidentally kept pruning older thinking on every later turn. The symptom looked like worse model quality, but the root cause was harness/session management.

For this project, the invariant is: **a successful resume is not proof of a successful continuation.** A continuation is healthy only if the agent acts on the latest user follow-up, reconciles with the job folder, and produces useful new evidence or a clear blocker. Pin that behavior in `RunPlanner`, `RunOutcomePolicy`, and session-event tests before touching per-CLI drivers.

Claude and Codex are the reference paths for stale-session work. Gemini and Copilot inherit the general recovery contract, but do not drive the next iteration until Claude and Codex are solid.

## Marker-line vocabulary

The frontend's [`activity-log.parser`](../../../frontend/src/app/components/activity-log.parser.ts) classifies output lines based on a leading-marker convention. Drivers' `TransformReadLine` must emit lines that match this vocabulary, or they fall into the `message` / `other` bucket.

| Marker | Kind | Example |
|---|---|---|
| `● Read <path>` | `read` | `● Read /repo/src/foo.ts` |
| `● Write <path>` | `edit` | `● Write /repo/src/foo.ts` |
| `● Edit <path>` | `edit` | `● Edit /repo/src/foo.ts` |
| `● Search <pattern>` | `search` | `● Search "TODO"` |
| `● Search glob <pattern>` | `search` | `● Search glob src/**/*.ts` |
| `● Search web <query>` | `search` | `● Search web claude api docs` |
| `● Run <command>` | `command` | `● Run npm test` |
| `● Fetch <url>` | `command` | `● Fetch https://example.com` |
| `● Todo update` | `todo` | (used by Claude's `TodoWrite` tool) |
| `● Task <description>` | `task` | (used by Claude's `Task` tool) |
| `● Session init <uuid> (<model>)` | `message` | Claude/Gemini session frame; captured via regex. Keep the shape stable. |
| `● Session <uuid>` | `message` | Codex session frame (`thread.started`). Captured from the raw line, not this marker. |
| `● Result <subtype>` | `message` | Claude final-result frame. |
| `● Turn completed (tokens: N)` | `message` | Codex `turn.completed`; analogue of Claude's `● Result (success)`. |
| `● Turn failed: <reason>` | `message` | Codex `turn.failed`; emitted on `stderr`. |
| `● Rate limit · …  [window=… status=… resetsAt=… overage=… usingOverage=…]` | `message` | Anthropic-only. The bracketed kv tail is parsed back into a typed `ClaudeRateLimitSnapshot` for the UI pill. |

Continuation lines (`  | <details>` or anything starting with whitespace) attach to the preceding marker as a subtitle / extra body line.

**Don't invent a new marker shape.** If you need a new tool kind, decide which existing kind it falls into (`read` / `search` / `command` / `edit`) and emit one of the markers above. Adding a parser branch ties the parser to a specific CLI.

## Unified renderer layer

The marker lines above are produced by `ICliOutputRenderer`, the marker-line twin of
the typed-event `*EventAdapter` classes (ADR-0013). Both are pure, dependency-free
mappers over a single CLI frame; the difference is the target — the adapter emits
`CliRunEvent`s for the bus, the renderer emits `CliOutputLine` marker lines for the
Activity Log.

```text
            raw CLI stdout/stderr line
                      │
        ┌─────────────┴──────────────┐
        ▼                            ▼
MapLineToRunEvents(raw)      TransformReadLine(raw)
   → *EventAdapter.Map          → ICliOutputRenderer.Render
   → CliRunEvent (bus)          → marker lines (Activity Log)
```

Files live in [`src/AgentTaskboard.Runner/Cli/Rendering/`](../../../src/AgentTaskboard.Runner/Cli/Rendering):

- `ICliOutputRenderer` — `IEnumerable<CliOutputLine> Render(CliOutputLine raw)`. Pure: no side effects, no state across calls. Session/telemetry capture is a side effect and stays in the driver's `OnOutputLine` / `MapLineToRunEvents` hooks, never here.
- `CliMarkerFormat` — stateless string primitives shared by every renderer (`SplitLines`, `TrimSingleLine`, `Truncate`, `FormatRelative`, the `●` bullet). Reuse these so the vocabulary stays byte-identical across CLIs instead of each driver re-deriving them.
- `ClaudeOutputRenderer`, `CodexOutputRenderer` — one per CLI.

**Why a strategy interface, not a base-class with overridable frame mappers** (the
AC asked for a justified preference): the per-CLI frame catalogues diverge enough
(Claude: `system`/`assistant`/`user`/`result`/`rate_limit_event`; Codex:
`thread.started`/`turn.*`/`item.completed`) that a shared base `switch` would be a
leaky abstraction. A pure renderer is `new()`-able with zero dependencies, so it
snapshot-tests per frame without the heavy `CodexCliService` constructor graph; it
mirrors the existing pure-static `*EventAdapter` pattern; and it keeps rendering out
of process orchestration. Shared logic is shared through the stateless
`CliMarkerFormat` helper, not through inheritance.

### How a new CLI adapter plugs in

1. Implement `ICliOutputRenderer` for the new CLI under `Cli/Rendering/`, mapping each
   frame to an existing marker in the vocabulary table above. Reuse `CliMarkerFormat`
   for splitting/trimming; **don't** invent new marker shapes.
2. In the driver, override `TransformReadLine` to delegate: `=> _renderer.Render(raw)`
   (one `private static readonly` renderer instance — it's pure).
3. Capture session-id / telemetry in `OnOutputLine` (reads the rendered marker) **or**
   `MapLineToRunEvents` (reads the raw frame), per § "Session model invariants". If the
   renderer rewrites the session frame, capture from the raw line.
4. Add a `<Cli>OutputRendererTests.cs` with one snapshot per frame type (frame in →
   marker out), plus encoding edge cases (umlauts/emoji, CR/LF, empty/long lines).

> **Not yet migrated:** `GeminiCliService` still carries its own inline `TransformReadLine`
> switch and `CopilotCliService` predates the base class. Both are out of this layer's
> current scope; migrating them onto `ICliOutputRenderer` is a clean follow-up that does
> not change their marker output.

## Output stream conventions

| Stream | Comes from | Frontend treatment |
|---|---|---|
| `stdout` | Process stdout (whatever the CLI writes) | Default. Markers + agent text. |
| `stderr` | Process stderr | Rendered as ERR / `error` kind. |
| `system` | Synthesized by the runner (`Started <cli>`, `<cli> exited`) | Rendered as SYS. |
| `user` | Synthesized when the user types a follow-up in the chat box (see `TaskRunnerService.AppendUserPromptToCliLog`) | Rendered as YOU; never folded into a tool group. |

`system` and `user` are runner-synthesized. Drivers must not produce them — `stdout`/`stderr` only. The base class strips ANSI escapes from persisted lines but does not strip them from the in-memory buffer; downstream consumers should handle either.

## Lifecycle invariants (apply to all four)

1. **Spawn closes stdin immediately.** Claude's `-p` mode reads stdin even when a prompt is provided and emits a 3 s "no stdin received" warning before continuing. We close stdin right after `Process.Start()` to skip that. Other CLIs do not need this but tolerate it. **Do not remove the close.**
2. **UTF-8 forced on redirected streams.** Default Windows code page (CP1252) corrupts non-ASCII bytes from Claude/Codex output and silently crashed runs that contained umlauts. The base class sets `StandardOutputEncoding = StandardErrorEncoding = UTF-8` plus `LC_ALL=C.UTF-8`, `LANG=C.UTF-8`, `PYTHONIOENCODING=utf-8`. Do not override per-CLI.
3. **Persist before notify.** The runner writes each line to `logs/cli-output.log` (JSONL) *before* invoking subscriber callbacks. A subscriber that throws cannot lose a line. The on-disk log is the durability guarantee `GetOutput` falls back to after a backend restart.
4. **Subscriber callbacks must not throw.** `OnOutput`/`OnFinished` exceptions used to crash the host. Both are wrapped in `try/catch` now; if you add a new subscriber, do the same.
5. **Synthetic Started/Exited lines.** Drivers must not skip the `[taskboard] Started <cli> CLI ...` and `[taskboard] <cli> CLI exited: ...` lines. The Activity Log used to be blank for 30+ s of Claude's `-p` buffering; users assumed the job was stuck.
6. **`ReapOrphans` on startup.** `CliExecutionServiceBase.ReattachOnStartup` kills any leftover CLI process from a previous backend run. PID-recycling is guarded by process name + start time. Copilot has its own version because it predates the base; keep both in sync if you change the contract.
7. **Output buffer cap = 5000 lines.** The in-memory `OutputBuffer` is trimmed to 5000 lines; longer history reads from `cli-output.log`. Don't grow this without thinking about RAM on long-running runs.

## Quota probes

Each CLI has a `QuotaProbeBase` subclass under `backend/Services/Quota/`. Probes run in scratch dirs under `%TEMP%/agent-taskboard-quota/<cliType>` (PTY-based for slash-command-driven probes; one-shot exec for command-driven). The aggregated result is served by `/api/cli/quota`.

**Common pitfalls when adding/changing a probe:**
- Never run a probe in the user's working directory. The CLI may pollute `~/.<cli>/sessions/` with junk runs and the disk-based session listing in `SessionRegistry` would surface them.
- Send `<Esc><Esc>` before tearing down a PTY-driven probe to close any open modal pickers. Several CLIs (Codex, Gemini) leave dialogs that block subsequent invocations.
- Probes refresh in the background; the user can force-refresh per-CLI via the side-sheet button.

## Common tasks

| Task | Where to start | Tests to add |
|---|---|---|
| Add a new tool marker for an existing CLI | The CLI's `ICliOutputRenderer` under `Cli/Rendering/` (Claude/Codex). Gemini still has an inline `TransformReadLine` switch. | A new snapshot test in the matching `*OutputRendererTests.cs` (or `*CliServiceTests.cs` for Gemini) |
| Add a new model to the catalog | `GetModelCatalogAsync` in the relevant driver, OR the live-discovery service for that CLI | None usually; live-discovery has integration tests already |
| Make resume work across a new edge case | `RunPlanner.PlanRun` — never the per-CLI driver | A new row in the `Plan_AlwaysProducesRunnableOutput` matrix in `TaskRunnerPlanTests.cs` |
| Capture a new piece of telemetry from CLI output | `OnOutputLine` in the relevant driver | A regex / parse test against a captured stream-json fixture |
| Add a brand-new CLI | [docs/cli/supported-clis.md](../supported-clis.md) §4 checklist | New `<Cli>CliServiceTests.cs` + new `@billable` E2E `frontend/e2e/<cli>-hello-world.spec.ts` |
| Fix a "session not captured" regression | First check `OnOutputLine` is wired and the regex matches the *transformed* line shape (Gemini/Claude). Run the relevant `TransformReadLine_*` tests against a real frame fixture. | Add a fixture-based regression test |

## Where state lives on disk

- **Runtime CLI output:** `<TaskRepository>/.runtime/cli-output/<cliType>-<jobKey>.jsonl` (per-job; deleted after summary write)
- **Active jobs (for orphan reaper):** `<TaskRepository>/.runtime/active-jobs-<cliType>.json`
- **Session indexes (each CLI's own store):**
  - Claude: `~/.claude/projects/<encoded-cwd>/<uuid>.jsonl`
  - Codex: `~/.codex/session_index.jsonl` + `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`
  - Copilot: `~/.copilot/history/<name>.jsonl` (when present)
  - Gemini: `~/.gemini/tmp/<project-slug>/chats/session-*.json` (slug map in `~/.gemini/projects.json`)
- **Quota scratch:** `%TEMP%/agent-taskboard-quota/<cliType>/...` (PTY scratch dirs)

`SessionRegistry` reads each store and serves `/api/cli/usage`. Adding a CLI means adding a `BuildXxxProjects()` method here.

## Test-fixture story

`backend.Tests/Fixtures/cli/<cliType>/` holds captured stream-json / output samples per CLI. Tests load these fixtures and run `TransformReadLine` over them, asserting marker output. Adding a fixture is the cheapest way to lock a behaviour we observed in the wild.

When you observe a new CLI behaviour (new frame shape, new error mode, new tool name), capture the raw stdout into the fixtures folder and add a regression test the same PR. The fixtures are the long-lived asset; the tests around them are the seatbelt.

## Things that are not in scope here

- Branch orchestration / multi-agent / per-task workspaces — see [AGENTS.md](../../../AGENTS.md) "Out of scope". The CLI drivers run **one** process per project at a time.
- Login flows. The task processor never drives auth; the user logs the CLI in out-of-band, the runner just spawns it.
- Stream-format wars. We translate every CLI's output to the marker-line vocabulary; we do not push back upstream to align CLI output formats.
