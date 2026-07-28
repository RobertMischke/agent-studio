# CLI fixtures — level-1 parity material for the CAR migration

Recorded coding-agent CLI transcripts plus a fake CLI that replays them. They
are the deterministic half ("Ebene 1") of the parity suite described in
`docs/operations/car-migration-plan.md` §3, tranche T3 (AGT-2372): no model call,
no network, no token spend — the same bytes fed to the legacy execution layer
today and to the CodingAgentRunner path after AGT-2370/2371, with the classified
outcome compared.

```
testdata/cli-fixtures/
  fake-cli.mjs      replays one fixture as if it were the CLI
  streams/          recorded transcripts, one file per scenario × form
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
| `scenario` | Parity scenario id from the plan (`P1` … `P22`). |
| `title` | One-line human description. |
| `cli` | `claude` or `codex` — whose frame vocabulary this is. |
| `form` | `stream-json` (structured frames) or `plaintext` (raw prose). |
| `exitCode` | Process exit code the replay ends with. |

File names follow `<scenario-slug>.<claude|codex|plaintext>.fixture`; the middle
token is `plaintext` for plaintext form, otherwise the CLI name. The mapping is
enforced by `runner.Tests/ParityFixtureTests.Every_recorded_fixture_is_well_formed`.

### Why `form` matters

The remote path today runs `claude -p` with **no** `--output-format`, so the
runner classifies raw prose (`docs/operations/setup/linux-runner-host.md`). The
CAR path emits `stream-json`. That is one of the five simultaneous behaviour
jumps of T1, and it is the reason P1 and P5 exist in a plaintext form as well:
the pair is what parity actually compares.

## Replaying a fixture

```bash
node testdata/cli-fixtures/fake-cli.mjs testdata/cli-fixtures/streams/p1-happy-done.claude.fixture
echo "the prompt" | node fake-cli.mjs streams/p1-happy-done.claude.fixture -p --output-format stream-json
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

`runner.Tests/ParityFixtureTests` currently pins P1 and P5 end to end and checks
that every fixture in the folder is well formed. P2, P3, P4, P9 and P22 are
recorded but not yet pinned — see "Still missing" below.

## Guard fixtures

`guard/violating-spawn.cs.txt` and `guard/compliant-spawn.cs.txt` are stored with
a `.txt` suffix so they are never compiled. They are fed to the scanner inside
`CliInvocationCentralizationGuardTests` (both instances) to prove the guard still
fires on a new raw CLI spawn and still stays silent on git plumbing and on a CLI
name that appears only in a comment.

## Still missing

Recorded but not yet asserted: P2, P3, P4, P9, P22.

Not yet recorded, because they are process or host facts rather than output
(they need a driver harness around `fake-cli.mjs`, not just a transcript):

* **P6** wall-clock timeout — `FAKE_CLI_DELAY_MS` plus a short run timeout.
* **P7** user stop / lease loss — kill the replay mid-stream.
* **P8** silence watchdog — `@delay` beyond the phase budget.
* **P10** provider auth failure, **P11** quota wait + resume, **P12** bounded
  same-session resume — need a fixture *pair* (first run, resumed run) and the
  capability-probe path.
* **P13**–**P21** — daemon restart, salvage, envelope trio, config-home
  isolation, token ledger, large prompt, kill path. These are level-2
  (Betriebsnachweis) or need the worker harness.
