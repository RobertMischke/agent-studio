# CLI fixtures - versioned provider capture corpus

Scrubbed coding-agent CLI transcripts plus a fake CLI that replays them. They
are the deterministic half ("level 1") of the parity suite described in
`docs/operations/car-migration-plan.md` section 3, tranche T3 (AGT-2372): no
model call, network, or token spend. The paired harnesses feed the same bytes to
the execution layer and to CodingAgentRunner, then compare typed lifecycle and
terminal results.

This is also the shared capture contract with Coding Agent Chat (CAC). Runner
tests consume provider semantics and outcomes. CAC consumes the same raw
stdout/stderr steps for rendering fixtures. Neither side stores a separately
rewritten transcript.

```
testdata/cli-fixtures/
  fake-cli.mjs      replays one fixture as if it were the CLI
  streams/
    <cli>/
      <version>/    captures, one file per scenario and form
  guard/            deliberately broken / deliberately clean C# sources that
                    keep CliInvocationCentralizationGuardTests honest
```

## Fixture grammar

A fixture is a UTF-8 text file with LF line endings.

| Line | Meaning |
|---|---|
| `#! { … }` | **Metadata**, JSON object. Exactly one per file, and it must be the first line that is neither blank nor a comment. |
| `# …` | Comment. Never replayed. |
| `@delay <ms>` | Wait `<ms>` extra milliseconds before the next replayed line. Models think-time and silence gaps (watchdog scenarios). |
| `!stderr <text>` | Replay `<text>` on **stderr**. |
| anything else | Replay the line verbatim on **stdout**, terminated by `\n`. |
| blank | Ignored. |

Metadata fields:

| Field | Meaning |
|---|---|
| `scenario` | Parity scenario id from the plan (`P1` through `P24`). |
| `title` | One-line human description. |
| `schemaVersion` | Capture contract version. Currently `1`. |
| `cli` | `claude` or `codex`, whose frame vocabulary this is. |
| `cliVersion` | Exact provider CLI version represented by the containing folder. |
| `form` | `stream-json` (structured frames) or `plaintext` (raw prose). |
| `exitCode` | Process exit code the replay ends with. |
| `durableOutputState` | Delivery fact supplied to outcome classification, currently `local-only` for recorded coding captures. |
| `captureSource` | `real-cli-stream` or the explicitly synthetic `synthetic-drift-probe`. |
| `capturedAt` | ISO calendar date of capture or synthetic probe creation. |
| `scrubbed` | Must be `true` before the fixture can be committed. |

Paths follow
`streams/<cli>/<cliVersion>/<scenario-slug>.<claude|codex|plaintext>.fixture`.
The middle filename token is `plaintext` for plaintext form, otherwise the CLI
name. The path and metadata mapping is enforced by
`runner.Tests/ParityFixtureTests.Every_recorded_fixture_is_well_formed`.

Scrubbing removes credentials, account identifiers, operator home paths,
private repository URLs, and unrelated prompt content. Preserve top-level and
nested frame type names, field casing, nullability, stream assignment, ordering,
exit code, and the minimum text needed to assert the terminal outcome. Synthetic
drift probes must say so in `captureSource`; they never masquerade as observed
provider frames.

### Why `form` matters

The former remote path ran `claude -p` with **no** `--output-format`, so the
runner classified raw prose. CAR emits `stream-json`. That was one of the five
simultaneous behaviour changes in T1, and it is why P1 and P5 retain a plaintext
form: the recordings pin the compatibility decision as well as the current
structured protocol.

## Replaying a fixture

```bash
node testdata/cli-fixtures/fake-cli.mjs testdata/cli-fixtures/streams/claude/2.1.202/p1-happy-done.claude.fixture
echo "the prompt" | node fake-cli.mjs streams/codex/0.144.1/p1-happy-done.codex.fixture exec --experimental-json
```

Everything after the fixture path is ignored, so the fake CLI can be dropped in
wherever a real binary name is configured.

| Environment variable | Effect |
|---|---|
| `FAKE_CLI_DELAY_MS` | Milliseconds to wait before every replayed line (default `0`). |
| `FAKE_CLI_EXIT_CODE` | Override the fixture's exit code. |
| `FAKE_CLI_FIXTURE` | Fixture path, when argv is not available. |
| `FAKE_CLI_CAPTURE` | Write a JSON record of argv, cwd, selected environment, and the stdin length/SHA-256 to this path — the hook for asserting prompt transport (argv vs. stdin, CAR-A) and size limits (P20). |
| `FAKE_CLI_NO_STDIN` | `1` skips reading stdin, modelling an argv-transport CLI. |

Exit code `64` is reserved: it means the fake CLI itself was misused (missing or
malformed fixture) and never comes from a fixture.

## Recorded scenarios

| Scenario | Files | What the recording pins |
|---|---|---|
| **P1** happy path | `p1-happy-done.{claude,codex,plaintext}.fixture` | Terminal `[[TASK_DONE]]` → `SentinelScanner` `Done`, lane `4-auto-review`; `ExecutionOutcomeAdapter` `SuccessfulCompletion` at high confidence. |
| **P2** no-op | `p2-noop.{claude,codex}.fixture` | `[[TASK_NOOP]]` → `NoOp`, lane `4-auto-review`, and the NoOp special case in `RemoteTaskRunner`. |
| **P3** blocked without reason | `p3-blocked-no-reason.{claude,codex}.fixture` | `[[TASK_BLOCKED]]` with no `:reason` → the synthesised default reason text, lane `5-human-review`. |
| **P4** needs input | `p4-needs-input.{claude,codex}.fixture` | `[[TASK_NEEDS_INPUT:choose-primary-column]]` → `NeedsInput` with the reason slug preserved, lane `5-human-review`. |
| **P5** no sentinel | `p5-no-sentinel.{claude,codex,plaintext}.fixture` | The divergence: plaintext → `ProtocolInconclusive` / `AskForHumanInput`; stream-json → `SuccessfulCompletion` at *medium* confidence, because a provider completion plus a final assistant reply is accepted as terminal evidence. Same scenario, two verdicts. |
| **P9** self-crash | `p9-self-crash.{claude,codex}.fixture` | Non-zero exit with a provider failure frame (`result subtype=error_during_execution` / `turn.failed`) → `CliCrash`. Worded deliberately free of quota/auth/model vocabulary so the diagnostic regexes cannot steal the case. |
| **P22** rate limit | `p22-rate-limit-{camel,snake}.claude.fixture`, `p22-rate-limit.codex.fixture` | Claude's `rate_limit_event` in both casings (CAR-E tolerance), including an ISO-8601 `resets_at` and a stringified boolean. The frame is informational: the run still ends `[[TASK_DONE]]`. Codex has no such frame — its limit arrives as a terminal `turn.failed` that today classifies as `QuotaExceeded` → `WaitForCapabilityRecovery`; recorded so the CAR path cannot quietly downgrade it to a plain crash. |
| **P23** protocol novelty | `p23-unknown-frame.{claude,codex}.fixture` | Synthetic unknown-frame probes appended to each real-version folder. Normal completion remains authoritative, while the frame produces `runner.protocol.unknown-frame` with scrubbed per-type and total counters. |
| **P24** native TODO list | `p24-todo-list.codex.fixture` | Codex `item.started` / `item.updated` snapshots with `item.type=todo_list`. Boolean completion becomes the normalized done/active/pending plan state while the final terminal outcome remains authoritative. |

`runner.Tests/ParityFixtureTests` pins P1 through P5, P9, and P22 through P24 and checks that
every fixture in the folder is well formed. `BackendLocalExecutionEngineParityTests`
and `RemoteWorkerEngineParityTests` provide the paired executable harnesses.

## Guard fixtures

`guard/violating-spawn.cs.txt` and `guard/compliant-spawn.cs.txt` are stored with
a `.txt` suffix so they are never compiled. They are fed to the scanner inside
`CliInvocationCentralizationGuardTests` (both instances) to prove the guard still
fires on a new raw CLI spawn and still stays silent on git plumbing and on a CLI
name that appears only in a comment.

## Process and host coverage

The output recordings are only one layer. Current deterministic coverage and
remaining acceptance gaps are:

| Scenario | Deterministic coverage | Remaining acceptance gap |
|---|---|---|
| **P6** timeout | Remote legacy and CAR workers replay the same delayed fixture and both return exit 124 with no surviving CLI PID. | Local wall-clock timeout is covered on CAR only. |
| **P7** user stop / lease loss | Local legacy and CAR executions compare `UserStop`; the runner also has an engine-neutral lease-loss process-group test. | No remote dual-engine user-stop injection exists at the detached-worker boundary. |
| **P8** silence watchdog | Local legacy and CAR executions compare `Watchdog`, alongside the phase-budget policy tests. | No full remote dual-engine silence-watchdog composition. |
| **P10** provider auth | Capability and typed-outcome policy tests pin authentication failures. | No paired executable worker replay through capability reporting. |
| **P11** quota wait | Host quota policy tests pin admission decisions. | CAR `WaitOnQuota`, `QuotaWaitStarted` and `QuotaWaitEnded` are not wired end to end in the Studio execution hosts. |
| **P12** bounded resume | Recovery policy and CAR continuation tests pin the single-resume bound and resume argv. | No paired remote first-turn/resumed-turn fixture. |
| **P13** daemon restart | Both detached-worker engines are reattached by a replacement daemon handle with continuous sequence numbers and one result. | A real `systemctl restart agent-host` remains operational evidence. |
| **P14** local restart | Orphan reaping and liveness demotion tests pin reap plus return to Ready. | A CAR-backed card interrupted by a real backend restart remains operational evidence. |
| **P15-P17** delivery, envelope and read-only lifecycle | Git workspace, durable handoff and completion-protocol tests cover salvage, fenced refs, digests, push verification and acknowledgement. | These host-owned steps are not yet composed with both execution engines in one harness; read-only epic teardown also lacks a direct runner test. |
| **P18** config isolation | Local legacy and CAR clean-home recipes are compared for excluded state, linked credential refresh and copied config. CAR worker launch tests pin the environment override. | Remote filesystem-level proof that the real home is never read is still missing. |
| **P19** token and cost ledger | The same local card fixture runs through both engines and reaches identical token, cost and post-step gate assertions. | Remote CAR metrics are not persisted through the detached result and ledger path. |
| **P20** large prompt | CAR launch replay proves a 200 KiB prompt travels on stdin rather than argv. | There is no old-versus-new paired transport comparison. |
| **P21** kill and cleanup | Paired local and remote executable tests assert the CLI PID is gone; host worktree tests cover process reaping before removal. | Dual-engine execution plus Git worktree cleanup is not composed in one remote test. |
