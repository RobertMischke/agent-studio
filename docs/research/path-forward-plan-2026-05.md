# Path-forward plan (2026-05)

**Status.** Synthesis-of-everything. Read after
[`cli-orchestration-survey-2026-05.md`](./cli-orchestration-survey-2026-05.md)
(per-project evidence, § R1-R5 named) and
[`wsl2-vs-windows-decision-2026-05.md`](./wsl2-vs-windows-decision-2026-05.md)
(the platform axis). Per-repo notes at
`c:/Projects/agent-taskboard-devspace/cli-source-references/*/NOTES.md`
are the concrete primary-source pointers; the survey is their index.

ADR boundaries this plan stays inside, named explicitly because every
recommendation below was checked against them:

- **ADR-0001** — sequential per project, parallel across projects.
- **ADR-0006 / ADR-0012** — subscriptions are the budget; no API keys.
- **ADR-0011** — Windows-native dev seat; `.CMD → .exe` for Claude;
  unproven root cause of the post-init silence.
- **ADR-0013** — typed `CliRunEvent` adapter contract; phase-aware
  watchdog; structured channels over PTY.
- **ADR-0014** — stale-session continuation is a first-class
  reliability target.

If you change one of those boundaries, this plan changes. If you don't,
this plan is the right one.

---

## 1. The shortest path to a non-hanging Claude run on Windows

### 1.1 Order of operations

The survey identified five recommended moves (R1-R5). Their priority
is not "size" or "complexity" but **evidence quality** and **risk-of-
regression**. Sequenced:

#### **Step 1 — R1 (stdin handling fix per claude-code#771).** *Immediate.*

**Why first.** Three independent OSS references converge on the
prescription
(`gate4agent/src/pipe/process.rs`,
`JeromySt-vscode-copilot-orchestrator/src/process/processHelpers.ts`,
`hoangsonww-AI-Agents-Orchestrator/orchestrator/adapters/cli_communicator.py`),
the upstream bug is filed and acknowledged-by-shape, and the .NET
reference (`github-copilot-sdk/dotnet/src/Client.cs:1200`) shows the
exact `RedirectStandardInput = options.UseStdio` pattern.

**Mapping.** `CliExecutionServiceBase.cs:225-272`. Two changes:

1. `psi.RedirectStandardInput =
   !string.IsNullOrEmpty(GetPromptStdinPayload(...))` — only redirect
   when we actually have a payload to write.
2. When we *do* have a payload, write it synchronously and close
   stdin *before* attaching the stdout/stderr read tasks. The
   ordering in the current code is OK, but the close-after-spawn
   race is the suspect.

**Test budget.** ~50 LOC + two tests:

- `CliWatchdogIntegrationTests.cs` — deterministic: a fake-CLI Node
  script that prints the init frame, waits 5 s, then prints "got
  EOF on stdin" or "got data on stdin." The new spawn path produces
  "got EOF" reliably.
- `CliSpawnIntegrationTests.cs` — live (gated `RUN_CLI_INTEGRATION=1`):
  re-runs the original failing case under `WebApplicationFactory<Program>`
  hosting (survey § "Open questions" #1). This is the missing
  reproducer; if it produces the symptom *before* R1, R1 should
  resolve it.

Behind feature flag `CliRunner:DisconnectStdinByDefault=true` so we
can A/B if needed. Default to true after the live probe confirms.

**Effort.** 0.5–1 day. **Risk.** Low.

#### **Step 2 — R2 (pre-emptive trust-store + env hardening).** *Immediate, parallel with R1.*

**Why parallel.** Independent of R1, addresses three separate
silent-blocking-dialog symptoms with the same shape
(survey § P5; CAO is the canonical reference).

**Mapping.** New `backend/Services/Cli/CliEnvironmentHardening.cs`:

- `EnsureClaudeSettings()` — idempotent; sets
  `~/.claude/settings.json::skipDangerousModePermissionPrompt=true`
  if not already.
- `EnsureGeminiTrustedFolders(taskRepoRoot)` — idempotent; adds
  `TRUST_PARENT` for the workspace tree.
- `BuildScrubbedEnv(parentEnv, cli)` — for Claude: drop `CLAUDE*`
  except `CLAUDE_CODE_USE_*` and `CLAUDE_CODE_SKIP_*_AUTH`. For all
  CLIs: drop `NODE_DEBUG` (`github-copilot-sdk/Client.cs:1216`),
  `NODE_OPTIONS` (`JeromySt/.../copilotCliRunner.ts:494`).

Called once on `TaskRunnerService.ExecuteAsync` startup, plus from
each adapter's `BuildStartInfo` for the env scrub.

**Test budget.** ~80 LOC + 6 unit tests (idempotency, no-op when
already set, scrub correctness per CLI). Zero behavioural risk
because the operations are idempotent.

**Effort.** 0.5–1 day. **Risk.** Very low.

#### **Step 3 — Probe: `WebApplicationFactory` repro of #771.** *Immediate, before deciding next.*

The single most important diagnostic step we have not yet done. If
R1 is the right fix, this probe **reliably reproduces the symptom**
in a deterministic test before R1, and **reliably no longer
reproduces it** after R1. If it doesn't reproduce the symptom even
without R1, our trigger is something else entirely and we should
stop and re-think before R3-R5.

**Mapping.** `backend.Tests/CliSpawnIntegrationTests.cs` — add a
new test fixture that uses
`Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>`
to spin up the backend in-process (rather than using xUnit's
default `dotnet test` host) and drive Claude through it. This
matches the *production process shape* the user reports the hang in.

**Effort.** 0.5 day. **Risk.** Low — this is pure diagnostics.

#### **Step 4 — R3 (per-CLI init silence budgets in PhaseAwareWatchdog).** *Near-term.*

**Why fourth.** Independent of R1/R2 but only valuable *after* we
can be sure R1/R2 fixed the dominant trigger; otherwise the watchdog
budgets just delay the symptom. CAO's empirical numbers (240 s for
Gemini due to MCP-on-first-launch) are the substrate.

**Mapping.** Extend `RunPhaseTransitions.cs` (ADR-0013's enum) so
each phase has its own per-CLI budget. Lookup table:

```
Cli:Claude:Phases:SessionInitializing  = 30s
Cli:Claude:Phases:TurnInProgress       = 120s
Cli:Gemini:Phases:SessionInitializing  = 240s   ← critical
Cli:Codex:Phases:SessionInitializing   = 60s
Cli:Copilot:Phases:SessionInitializing = 60s
```

**Test budget.** ~120 LOC + 4 deterministic tests (one per CLI,
fake-CLI script that stays silent for 90 % of the budget then emits
the expected event).

**Effort.** ~0.5 day. **Risk.** Low.

#### **Step 5 — Optional: `WindowsHandleScrub.cs` P/Invoke.** *Conditional on Step 3 result.*

**Only do this if Step 3's `WebApplicationFactory` probe reproduces
the symptom *and* R1 doesn't make it go away.** That outcome would
indicate the trigger is not stdin alone but some other handle the
.NET `Process` API doesn't expose for us to scrub.

**Mapping.** New `backend/Services/Cli/WindowsHandleScrub.cs` using
P/Invoke to `STARTUPINFOEX` +
`UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_HANDLE_LIST)` so
we explicitly pass an empty handle-inheritance list (or only the
three CLI stdio handles) instead of letting `bInheritHandles=TRUE`
inherit everything inheritable.

**Effort.** 1–2 days. **Risk.** Medium (P/Invoke is finicky; have
to test on Windows 10 *and* 11 because handle attribute support
differs).

#### **Steps 6+ — R4 (ACP) and R5 (long-lived Claude).** *Medium-term.*

Distinct architectural shifts. Not in the "make hang go away" path;
in the "make the platform clean" path. Treated as separate work.
See § 4 below for sequencing.

### 1.2 Sequenced summary

| #   | Move                       | Days  | Cumulative | Decision-point               |
| --- | -------------------------- | ----- | ---------- | ---------------------------- |
| 1   | R1 stdin fix               | 0.5–1 | 0.5–1      | Default after probe          |
| 2   | R2 env hardening           | 0.5–1 | 1–2        | Default                      |
| 3   | WebApplicationFactory probe| 0.5   | 1.5–2.5    | Gate to step 5               |
| 4   | R3 per-CLI budgets         | 0.5   | 2–3        | Default                      |
| 5*  | WindowsHandleScrub P/Invoke| 1–2   | 3–5        | Only if step 3 says we need  |
| 6   | R4 ACP transport (Gemini)  | 3     | 6–8        | Architectural; survey § R4   |
| 7   | R5 long-lived Claude       | 5     | 11–13      | Architectural; survey § R5   |

Steps 1-4 are the **shortest path to a non-hanging Claude run on
Windows**. Step 5 is the contingent fallback. Steps 6-7 are
roadmap items that interact with the WSL2 axis below.

---

## 2. Where the WSL2 axis splits the path

Cross-reference: [`wsl2-vs-windows-decision-2026-05.md`](./wsl2-vs-windows-decision-2026-05.md).

If the user (or their successor) decides to **require WSL2** instead
of staying Windows-native:

| Step | Required on Windows-native?     | Required on WSL2-only?        | Why                                                                    |
| ---- | ------------------------------- | ----------------------------- | ---------------------------------------------------------------------- |
| 1    | Yes                             | **Yes**                       | claude-code#771 affects Node spawn on Linux too                         |
| 2    | Yes                             | **Yes**                       | Trust-store dialogs and env-leak issues are platform-agnostic          |
| 3    | Yes                             | Replace with Linux-host probe | The `bInheritHandles` shape is Windows-only; on Linux the suspect is gone |
| 4    | Yes                             | **Yes**                       | Per-CLI init budgets are CLI-specific, not OS-specific                  |
| 5    | Conditional                     | **Moot — not needed**         | `posix_spawn_file_actions_addclose` is the Linux equivalent and it works |
| 6    | Yes                             | **Yes**                       | ACP is OS-agnostic                                                      |
| 7    | Yes                             | **Yes**                       | Long-lived processes are OS-agnostic                                    |
| -    | `.CMD → .exe` rewrite (already done) | **Moot — npm doesn't ship `.CMD` on Linux** | -                                          |
| -    | tmux-substrate option           | **Becomes plausible**         | Survey § N1's blocker (Windows tmux) goes away                          |

So the WSL2 axis primarily eliminates **step 5** (Windows handle
scrub) and the latent option of using tmux. Everything else still
applies. **Required-WSL2 saves at most ~1-2 days of P/Invoke work
and adds 3-6 days of contributor onboarding**, which is why the
WSL2 decision document recommends staying Windows-native.

---

## 3. Long-term reference projects (keep watching)

Out of the 15 cloned reference projects, three are worth keeping as
**ongoing references** beyond just this hang:

### 3.1 `ZENG3LD/gate4agent`

**Why.** Closest architectural analogue to where ADR-0013 wants us
to land. Three transports (`pipe/`, `pty/`, `acp/`), per-CLI builders,
typed events. Active development; new transports added if upstream
CLI vocabularies change. **Track its `acp/spawn.rs`** when we do
survey § R4 — they'll absorb shim-version bumps for us before we
hit them.

See [`cli-source-references/ZENG3LD-gate4agent/NOTES.md`](../../cli-source-references/ZENG3LD-gate4agent/NOTES.md).

### 3.2 `awslabs/cli-agent-orchestrator`

**Why.** The richest per-CLI workaround library in the survey. They
hit every interactive-blocking-dialog symptom before we did and
documented the fixes. **Track their `providers/<cli>.py` files**
when we add a new CLI or when a CLI we already support emits a new
init-time dialog (Anthropic and Google ship these regularly).

See [`cli-source-references/awslabs-cli-agent-orchestrator/NOTES.md`](../../cli-source-references/awslabs-cli-agent-orchestrator/NOTES.md).

### 3.3 `github/copilot-sdk` (specifically `dotnet/src/`)

**Why.** Microsoft's own first-party .NET reference for spawning a
CLI as a JSON-RPC server. The closest to our environment we have.
Their `Client.cs` is the canonical .NET implementation of all the
spawn-discipline patterns we need. If we adopt R4 with Copilot in
scope, this is the SDK we'd embed (or vendor framing helpers from).

See [`cli-source-references/github-copilot-sdk/NOTES.md`](../../cli-source-references/github-copilot-sdk/NOTES.md).

### What about the others?

- `unixfox/opencode-claude-code-plugin` — **canonical reference for
  R5 (long-lived Claude)**. Worth re-reading when we implement R5;
  not before. AGPL-3.0 means read-only.
- `openai-codex` — protocol schema reference; consult when we
  implement Codex side of `CliRunEvent` (R4 phase 2).
- `anthropics-claude-code` — changelog only; consult on every
  Claude package version bump.
- `kingbootoshi-codex-orchestrator`, `microsoft-vscode-copilot-chat`
  — useful for ADR-0014 (stale-session) work specifically.
- `aannoo-hcom`, `JeromySt-vscode-copilot-orchestrator`,
  `hoangsonww-AI-Agents-Orchestrator`, `sst-opencode`,
  `lucad87-gemini-orchestrator`, `Aider-AI-aider`,
  `github-copilot-cli` — read once; re-read only if a specific
  pattern question recurs.

---

## 4. Do-not-pursue list (specific to our context)

Each item below is a pattern that *looks attractive* in some survey
section but does not fit our product boundaries. Cited so future
contributors / agents don't have to re-derive the rejection.

### 4.1 Subscription billing → API keys

Several Claude/Codex SDKs in the ecosystem (Anthropic Agent SDK,
OpenAI Agents SDK, sst/opencode's core loop, Aider) use API keys
directly. **Forbidden by ADR-0006 / ADR-0012.** We orchestrate
*subscription-backed coding agents*. If a path forward seems to
require API keys, it is the wrong path. Cross-reference: survey
§ N3.

### 4.2 Multi-agent fan-out / branch-per-task / worktrees

`hcom`'s message bus, `kingbootoshi-codex-orchestrator`'s
`codex-bg` parallel-agents shape, `JeromySt`'s worktree
orchestration, `hoangsonww`'s agentic-team layer — all interesting,
all violate **ADR-0001** (sequential per project). Cross-reference:
survey § N4. If a user asks about this in chat, mention it as
"interesting but explicitly off-roadmap."

### 4.3 tmux / screen-scraping as primary substrate

`awslabs/cli-agent-orchestrator`, `kingbootoshi/codex-orchestrator`,
parts of `aannoo/hcom`. Requires WSL2 on Windows; ADR-0011 says no.
**The workarounds these projects apply** (env scrub, trust-store
seeds, per-CLI budgets) are absorbed into our R2/R3 instead. The
substrate is rejected. Cross-reference: survey § N1.

### 4.4 PTY-everything

ADR-0011 records that we tried PTY for Claude `-p` and it failed
because Claude exits when stdin is a TTY. The PTY hook in
`SpawnChildAsync` is kept for genuinely-interactive CLIs (Copilot's
existing `PtySession` is the example). Don't widen its application
without a fresh probe per CLI. Cross-reference: survey § N2.

### 4.5 SQLite-as-message-bus

`hcom`'s SQLite `instances` table. Solves a problem we don't have.
Our `active-jobs-{cli}.json` files are right-sized.
Cross-reference: survey § N5.

### 4.6 Long-lived global daemon process per CLI

If we adopt R5 (long-lived Claude), the simplest viable shape is
"kill on idle after N minutes" — *not* a process daemon. Daemon
semantics interact badly with the orphan reaper. Cross-reference:
survey § N6.

### 4.7 Required-WSL2 dev seat

Documented in [`wsl2-vs-windows-decision-2026-05.md`](./wsl2-vs-windows-decision-2026-05.md).
Recommendation is **Windows-native + platform-conditional code
paths + WSL2 documented as alternative**. Don't require it.

### 4.8 Vendoring AGPL-3.0 code

`unixfox/opencode-claude-code-plugin` is AGPL-3.0. Reading patterns
is fine; *vendoring any line of code from it would make our backend
AGPL-3.0*. Re-implement, do not copy. Other licenses (MIT,
Apache-2.0) in the survey are compatible but we still prefer pattern
transfer over vendoring; explicit dependency adds maintenance cost.

---

## 5. Risk register

What could still bite us after R1-R5 are done.

### 5.1 Anthropic-side stream-json rate limit (suspect D)

**Hypothesis.** Anthropic caps concurrent stream-json sessions per
account. Our backend plus a user's interactive Claude run hit the
cap, our spawn succeeds (HTTP-level), prints the init frame, then
the *next* request returns a quietly-throttled response that
Claude waits on indefinitely.

**Probe.** Sequentialise — only spawn a Claude when no other
Claude is alive in this account — and see if hangs disappear.
Cross-reference: survey § "Open questions" #4.

**Mitigation.** ADR-0001 already gives us sequential-per-project;
add per-account sequentiality across projects if probe confirms.
Cost: 1-2 days.

### 5.2 Concurrent Claude in the same per-cwd directory (suspect B)

**Hypothesis.** Two Claude processes sharing
`~/.claude/projects/<encoded-cwd>/` race on the session JSONL file.
Cross-reference: survey § "Open questions" #2.

**Probe.** Add a unit test that spawns two Claude processes with
the same cwd and checks for the symptom. Already named in ADR-0011's
follow-ups.

**Mitigation.** If reproduces, document and either (a) sequentialise
per-cwd at our orchestrator layer, (b) work with Anthropic to
report. Cost: 1 day to probe.

### 5.3 CLI version drift breaking flag stability

**Hypothesis.** Anthropic / OpenAI / Google / GitHub bump CLI
versions; flags change shape (e.g. Copilot CLI's `--max-turns`
removal in v1.0.31). Our pinned-flag set silently breaks.

**Mitigation.** A "probed-flags" cache in each CLI service
(JeromySt's pattern). On version bump, re-probe. Cost: ongoing
maintenance ~0.5 day per breaking bump.

### 5.4 `~/.claude/projects/<encoded-cwd>` filename encoding drift

**Hypothesis.** Claude changes its cwd-encoding scheme (slash
substitution, length truncation), our session-file lookup goes
stale. ADR-0014 already considers this.

**Mitigation.** Fingerprint by content (most-recently-modified file
in the right directory), not by exact filename match. Already partial.

### 5.5 Anthropic / OpenAI / Google subscription auth flow drift

**Hypothesis.** Auth flows shift (device code, OAuth, browser
redirects). Our cached login expires; the CLI prompts interactively
on next launch; we hang. Same symptom shape as the post-init
silence.

**Mitigation.** R3's per-CLI init budgets + diagnostic dump on
timeout (CAO's pattern). The hang then surfaces as a typed
`NeedsInput` event with a captured pane buffer, not a watchdog kill
with no info.

### 5.6 ConPTY changes between Windows builds

**Hypothesis.** Microsoft ships ConPTY changes in Windows feature
updates that break our (currently latent) PTY hook. Low likelihood
but non-zero.

**Mitigation.** PTY use is currently scoped to Copilot's
`PtySession`. Extend integration tests to cover the active PTY
paths. Cost: low, maintenance.

### 5.7 .NET runtime patch behaviour drift

**Hypothesis.** A .NET 8/9 patch changes how `Process.Start`
inherits handles or buffers redirected streams. `.NET` is not as
tightly version-controlled in production as Node.

**Mitigation.** Pin `global.json`'s SDK; CI on the pinned runtime.
Cost: zero, already standard.

### 5.8 Stdin pipe write race after R1 (regression risk for Claude payload path)

**Hypothesis.** R1 changes the spawn-then-write order. If the
write happens *after* Claude has already EOF'd its stdin (because
`Process.Start` returned, Claude raced ahead, decided "no stdin
data" before we wrote our prompt), the Claude path gets the prompt
on argv but *not* on stdin, and the prompt is silently dropped.

**Mitigation.** R1's tests must cover the "prompt is read by
Claude" case explicitly, not just "stdin is closed." Already named
in step 1's test budget.

---

## 6. Recommended cadence and decision-points

- **Week 1:** Steps 1–4 (R1, R2, probe, R3). 2-3 days of focused
  work. **Decision-point:** does the WebApplicationFactory probe
  reproduce the symptom? Does R1 fix it?
- **Week 2:** Decision-point determines step 5 (`WindowsHandleScrub`)
  vs. proceed to step 6 (R4 ACP). If the hang is gone, R4 becomes
  architectural improvement, not crisis-mitigation.
- **Week 3-4:** Step 6 (R4 ACP for Gemini) — **only if** the hang is
  resolved and we have time. If the hang persists despite R1+R2+5,
  stop and re-think; do not stack R4 on a broken foundation.
- **Month 2+:** Step 7 (R5 long-lived Claude). Larger architectural
  shift; depends on step 6 going smoothly because R5 reuses the
  multi-turn process model that R4 introduces.

---

## 7. Ongoing-maintenance items not on the timeline

These are forever-tasks; they don't have an "ours" or "theirs":

- Bump Claude / Codex / Gemini / Copilot CLI versions and re-run
  `RUN_CLI_INTEGRATION=1`.
- Keep `cli-source-references/*/NOTES.md` current when an upstream
  reference changes shape (gate4agent's ACP spawn table; CAO's
  per-CLI init budgets).
- Watch
  [`anthropics/claude-code` issues](https://github.com/anthropics/claude-code/issues)
  for new symptom shapes. `#771` is the canonical one; future ones
  will appear and we want to recognise them.
- When `OperatingSystem.IsWindows()` branches start leaking from
  `Services/Cli/` into other namespaces, revisit
  [`wsl2-vs-windows-decision-2026-05.md`](./wsl2-vs-windows-decision-2026-05.md)
  § 5 cost analysis.

---

## 8. Cross-references

- [`cli-orchestration-survey-2026-05.md`](./cli-orchestration-survey-2026-05.md)
  — primary evidence index.
- [`wsl2-vs-windows-decision-2026-05.md`](./wsl2-vs-windows-decision-2026-05.md)
  — platform axis.
- Per-repo NOTES under
  `c:/Projects/agent-taskboard-devspace/cli-source-references/*/NOTES.md`.
- ADR-0011 (CLI-process spawn boundary), ADR-0012 (existing engines),
  ADR-0013 (typed events), ADR-0014 (stale-session reliability) in
  [`docs/architecture-decisions.md`](../architecture-decisions.md).
- [`backend/Services/Cli/CliExecutionServiceBase.cs`](../../backend/Services/Cli/CliExecutionServiceBase.cs)
  — the file most of these moves touch.

---

## 9. One-paragraph summary

R1 (stdin handling per claude-code#771) and R2 (env hardening per
CAO's playbook) are the immediate moves; both are platform-agnostic,
both have multiple converging upstream references, both should land
this week. The single load-bearing diagnostic gap is the
`WebApplicationFactory`-shaped reproducer (survey § "Open questions"
#1), which is half a day of work and tells us whether R1 is a
sufficient fix or whether we additionally need a Windows-specific
P/Invoke for `STARTUPINFOEX` handle scrubbing. WSL2 is documented as
an alternative but **not required** because (a) the dominant
suspect class is platform-agnostic and R1 fixes it on both platforms,
(b) the user's working environment is Windows-native, and (c) the
contributor-onboarding cost of required-WSL2 outweighs the ~1-2 days
of P/Invoke work it would save. R4 (ACP transport, Gemini first) and
R5 (long-lived Claude) are roadmap items that follow the hang fix,
not prerequisites for it. Multi-agent fan-out, API-key billing,
tmux-substrate, and SQLite-as-message-bus remain explicitly off-
limits.
