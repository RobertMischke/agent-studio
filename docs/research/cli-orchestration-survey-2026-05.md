# CLI orchestration survey (2026-05)

**Mission.** Survey OSS projects that orchestrate the same four CLIs we drive
(Claude Code, Gemini CLI, Codex, GitHub Copilot CLI), with focus on the
"first protocol frame, then 60-180s silence" failure mode that reproduces
inside our ASP.NET-hosted backend but **not** under shell direct-invocation
or `dotnet test` child-process spawn.

Reading order: **smoking-gun primary source** ▸ **per-project analyses** ▸
**patterns** ▸ **recommendations** ▸ **anti-patterns**.

This document is the **master index** for a three-part deliverable:

- **This file** (`cli-orchestration-survey-2026-05.md`) — per-project
  evidence and patterns. Recommendations R1-R5 are named here.
- [`wsl2-vs-windows-decision-2026-05.md`](./wsl2-vs-windows-decision-2026-05.md)
  — the platform-axis decision (WSL2 vs Windows-native vs both).
  ADR-0015-candidate.
- [`path-forward-plan-2026-05.md`](./path-forward-plan-2026-05.md) —
  synthesis: shortest path, sequencing, risk register.

Per-repo primary-source notes live next to each cloned repo at
`c:/Projects/agent-taskboard-devspace/cli-source-references/<repo>/NOTES.md`.
Each NOTES.md cross-links back into this index.

Authoritative scope context lives in
[`docs/architecture/decisions/adr-archive.md`](../architecture/decisions/adr-archive.md) ADR-0011
(unproven .CMD root-cause), ADR-0012 (existing agents are the engine),
ADR-0013 (typed `CliRunEvent` + phase-aware watchdog), and ADR-0014
(stale-session reliability is first-class). All recommendations below stay
inside those boundaries.

---

## Executive summary (TL;DR)

There is **one piece of direct upstream evidence** that the symptom we see is
a *real, known bug class for any process spawning Claude Code as a child*,
and it is **not** specific to .NET: Anthropic issue
[claude-code#771](https://github.com/anthropics/claude-code/issues/771)
(**"[BUG] Claude Code can't be spawned from node.js, but can be from python"**)
documents the exact symptom — Claude emits init, then stalls for 30 s — and
the workaround is **`stdio: ['ignore', 'pipe', 'pipe']`** rather than the
default inherited stdin. Python's `subprocess.run(..., capture_output=True)`
internally sets stdin = DEVNULL, which is why the Python repro path works.
This is suspect **A** from the briefing, *now elevated from hypothesis to
documented-by-vendor*. Our base class today does
`RedirectStandardInput = true` then closes the handle (`child.Stdin.Close()`)
*after* `Process.Start` returns — which on Windows / .NET is **not** the
same as never connecting stdin in the first place. See
[`CliExecutionServiceBase.cs:226-272`](../../backend/Services/Cli/CliExecutionServiceBase.cs).

The two strongest *general* patterns across the surveyed orchestrators are:

1. **Adopt ACP (Agent Client Protocol) where possible.** Zed's open standard
   is JSON-RPC 2.0 over stdio with persistent stdin, and **all four of our
   CLIs already have ACP bindings** (Gemini native, OpenCode native, Claude
   via `@agentclientprotocol/claude-agent-acp`, Codex via
   `@zed-industries/codex-acp`). This is what `gate4agent` and Zed do, and
   it is structurally what ADR-0013 already endorses — but ACP gives us a
   ready, four-CLI typed protocol *today* without writing four bespoke
   adapters.
2. **For non-ACP fallback, give up on plain pipes for non-Claude CLIs.**
   Every project that successfully orchestrates Gemini or Codex headlessly
   ends up either (a) using a real PTY (hcom, gate4agent, opencode), (b)
   hosting the agent inside a tmux pane and screen-scraping (awslabs CAO,
   kingbootoshi codex-orchestrator), or (c) explicitly refusing to support
   the headless mode at all (hcom for Gemini headless `-p/--prompt`).
   Plain `Process.Start` with redirected pipes is the path with the most
   reported hangs across all surveyed projects.

The cleanest architectural move for *us* is therefore **ACP first for new
work, with the existing pipe path kept only as the Claude `--print`
fallback**, plus a stdin-handling fix that matches the
[claude-code#771](https://github.com/anthropics/claude-code/issues/771)
guidance for the cases where we still need to spawn the bare CLI.

---

## Direct upstream evidence: claude-code #771

**Issue.** [`anthropics/claude-code#771` — "[BUG] Claude Code can't be spawned
from node.js, but can be from python"](https://github.com/anthropics/claude-code/issues/771).

**Symptom.** `claude -p --output-format stream-json` stalls for 30 s with
zero output when spawned from Node `child_process.spawn` / `exec`. The same
command works from Python `subprocess.run(cmd, capture_output=True)` and
from a shell.

**Root cause (vendor-confirmed shape).** Default Node `spawn` inherits the
parent process's stdin. Claude Code reads stdin during init (even when the
prompt is on argv) and blocks waiting for input that never comes. Python's
`capture_output=True` internally sets `stdin = DEVNULL`; that is why the
Python path "just works."

**Recommended workaround in the issue.**

```javascript
spawn('claude', ['-p', '--output-format', 'stream-json', ...], {
  stdio: ['ignore', 'pipe', 'pipe'],   // ← stdin = ignore, NOT inherit
})
```

**Mapping to our codebase.** Our base class does the
moral-equivalent-but-not-actually-equivalent-on-Windows of:

```csharp
// CliExecutionServiceBase.cs:226-271
psi.RedirectStandardInput = true;     // creates a pipe…
…
try { /* maybe write payload */ }
finally { try { child.Stdin.Close(); } catch { } }   // …then closes it
```

The race is real on Windows: between `Process.Start` and the moment the
.NET runtime's stdin Stream is *closed*, the child process can already have
read 0 bytes from a *connected* stdin pipe and gone into "wait for more"
mode. .NET's `Process` API has no equivalent of Node's `'ignore'` — the
closest is to **not redirect stdin at all** (`RedirectStandardInput = false`)
**combined with** disconnecting the parent's stdin so it cannot inherit a
TTY. On a hosted ASP.NET process under `dotnet run` from a console, the
parent stdin is still attached to the controlling TTY; under `dotnet test`
the test runner already disconnects it. **That is the single best
explanation we have today for why the symptom fires under `dotnet run` but
not under `dotnet test`.**

This is suspect A from the briefing. It is now the highest-prior suspect.

---

## Per-project analyses

### 1. `unixfox/opencode-claude-code-plugin` (TypeScript, AGPL-3.0)

**What it orchestrates.** Claude Code only, as a "language model" provider
plugin for `sst/opencode`.

**Repo at:** `c:/Projects/agent-taskboard-devspace/cli-source-references/unixfox-opencode-claude-code-plugin`

**Spawn shape.** [`src/session-manager.ts:53-57`](../../../cli-source-references/unixfox-opencode-claude-code-plugin/src/session-manager.ts):

```typescript
const proc = spawn(cliPath, cliArgs, {
  cwd,
  stdio: ["pipe", "pipe", "pipe"],
  env: { ...process.env, TERM: "xterm-256color" },
})
```

**CLI args.** [`src/session-manager.ts:113-118`](../../../cli-source-references/unixfox-opencode-claude-code-plugin/src/session-manager.ts):
`--output-format stream-json --input-format stream-json --verbose` —
**both directions stream-json**, the bidirectional protocol mode.

**Process model.** *Persistent process per (cwd, model) pair*, not per turn.
Stdin stays open and is reused for follow-up turns. See
[`src/claude-code-language-model.ts:512-524`](../../../cli-source-references/unixfox-opencode-claude-code-plugin/src/claude-code-language-model.ts):

```typescript
let activeProcess = getActiveProcess(sk)
…
if (activeProcess) {
  proc = activeProcess.proc
  lineEmitter = activeProcess.lineEmitter
  log.debug("reusing active process", { sk })
} else {
  const ap = spawnClaudeProcess(cliPath, cliArgs, cwd, sk)
  …
}
```

**Stdin write per turn.** [`src/claude-code-language-model.ts:1091`](../../../cli-source-references/unixfox-opencode-claude-code-plugin/src/claude-code-language-model.ts):
`proc.stdin?.write(userMsg + "\n")` — never closed, just written-to.

**Watchdog / timeout strategy.** None. Relies on the `result` event in the
NDJSON stream as the natural turn-end. On consumer abort, the process is
**kept alive** for the next message:
[`src/claude-code-language-model.ts:1071-1088`](../../../cli-source-references/unixfox-opencode-claude-code-plugin/src/claude-code-language-model.ts):

```typescript
if (options.abortSignal) {
  options.abortSignal.addEventListener("abort", () => {
    if (!turnCompleted) {
      log.info("abort signal received mid-turn, keeping process alive", { cwd })
    }
    …
  })
}
```

**Anti-silence specifics.** Default `stdio: ['pipe', 'pipe', 'pipe']` plus
`TERM: 'xterm-256color'`. **This project doesn't actively work around the
hang because it doesn't reproduce the hang** — a Bun/Node child of a Bun
parent is a different stdin-inheritance shape than a .NET Kestrel host
spawning a Node child. Listed here as the canonical successful "long-lived
bidirectional stream-json over plain pipes" reference.

---

### 2. `aannoo/hcom` (Rust, MIT)

**What it orchestrates.** Claude Code, Gemini CLI, Codex, OpenCode — message
bus + spawning + transcript watching.

**Repo at:** `c:/Projects/agent-taskboard-devspace/cli-source-references/aannoo-hcom`

**Spawn taxonomy.** [`src/launcher.rs:84-110`](../../../cli-source-references/aannoo-hcom/src/launcher.rs):

```rust
pub enum LaunchBackend {
  InteractiveVisible,   // foreground TTY for user-visible terminal
  HeadlessPty,          // background, full PTY wrapper — default for gemini/codex/opencode
  NativePrint,          // background, claude -p stream-json — claude only, one-shot
}
```

The note at line 102-108 is load-bearing: **Claude is the *only* CLI hcom
will run in `NativePrint` (= our path today). Every other CLI goes through
`HeadlessPty`.** Their authors made the same decision Zed and gate4agent
made: don't try to drive Gemini / Codex via redirected pipes.

**Gemini headless mode is explicitly rejected.**
[`src/tools/gemini_args.rs:484-498`](../../../cli-source-references/aannoo-hcom/src/tools/gemini_args.rs):

```rust
"ERROR: Gemini headless mode (-p/--prompt flag) not supported in hcom.
Use -i/--prompt-interactive for interactive sessions with initial prompt."
```

This is a **production project that orchestrates Gemini and refuses to ship
headless support for it**. Strong signal that Gemini's
`--output-format stream-json -p` lane is, in their experience, not worth the
maintenance.

**PTY shape (Unix only).** [`src/pty/mod.rs:533-590`](../../../cli-source-references/aannoo-hcom/src/pty/mod.rs):
`nix::pty::openpty` + classic `setsid` / `TIOCSCTTY` / `dup2` of slave to
fd 0/1/2. The master fd is explicitly closed in the child to ensure SIGHUP
on PTY teardown — relevant if we ever do PTY (we have a working
counterexample: per ADR-0011 we tried PTY for Claude `-p` and it failed
because Claude exits when stdin is a TTY).

**Watchdog strategy.** No silence-based watchdog. The screen-tracker
detects "ready pattern" + "user activity cooldown" and lets the session run
indefinitely; orphans are reaped from a SQLite `instances` table the
launcher populated.

**Transferable patterns.**
- Three named backends with clear preconditions per CLI.
- "We refuse to support this lane" as an honest stance.
- Persistent SQLite-tracked PIDs for orphan reaping (we already do this in
  `active-jobs-{cli}.json`).

---

### 3. `ZENG3LD/gate4agent` (Rust, Apache-2.0)

**What it orchestrates.** Claude Code, Gemini CLI, Codex, OpenCode — three
**parallel** transports: `pipe/`, `pty/`, `acp/`. This is the closest
architectural analogue to what ADR-0013 already specifies.

**Repo at:** `c:/Projects/agent-taskboard-devspace/cli-source-references/ZENG3LD-gate4agent`

**Pipe transport.** [`src/pipe/process.rs:74-125`](../../../cli-source-references/ZENG3LD-gate4agent/src/pipe/process.rs):

```rust
cmd.stdin(Stdio::piped());
cmd.stdout(Stdio::piped());
cmd.stderr(Stdio::null());            // ← stderr *discarded*

let mut child = cmd.spawn()?;
let stdin = child.stdin.take();

if let Some(mut s) = stdin {
  if tool == CliTool::ClaudeCode {
    s.write_all(initial_prompt.as_bytes())?;
    s.flush()?;
  }
  drop(s); // close stdin → Claude sees EOF → starts processing
}
```

The `drop(s)` drops the parent's stdin handle *unconditionally* for all four
tools. Claude gets the prompt before drop; Codex/Gemini/OpenCode get the
prompt on argv. The comment "**Claude `-p` reads stdin until EOF, so we
must drop (close) stdin after writing**" matches the upstream
[claude-code#771](https://github.com/anthropics/claude-code/issues/771)
guidance.

**Windows handling — explicitly different from our ADR-0011 approach.**
[`src/pipe/process.rs:155-219`](../../../cli-source-references/ZENG3LD-gate4agent/src/pipe/process.rs):
gate4agent **keeps the `cmd /C <program>.cmd <args>` wrap** but passes each
arg as a **separate** element of the Command, never joining them into a
shell string. They argue that's the only way Windows CreateProcess handles
quoting correctly for prompts that contain spaces and special characters.
Our ADR-0011 went the *other* direction (rewrite to underlying `.exe`),
explicitly because the `cmd /C` wrap was suspected. Both approaches work in
isolation; gate4agent's choice is a counter-example evidence that the
`.CMD` wrap is *not necessarily* the cause of our hang.

**Per-CLI builders.** Separate files per CLI keep argv generation cohesive:
- [`src/pipe/cli/claude.rs:236-295`](../../../cli-source-references/ZENG3LD-gate4agent/src/pipe/cli/claude.rs)
  (`ClaudePipeBuilder`, prompt via stdin)
- [`src/pipe/cli/gemini.rs:199-231`](../../../cli-source-references/ZENG3LD-gate4agent/src/pipe/cli/gemini.rs)
  (`GeminiPipeBuilder`, `-p <prompt>` on argv)
- [`src/pipe/cli/codex.rs`](../../../cli-source-references/ZENG3LD-gate4agent/src/pipe/cli/codex.rs)

The shape is one-to-one with our `Adapters/<Cli>EventAdapter.cs` plus an
argv builder, which is the structure we'd land on anyway.

**ACP transport.** [`src/acp/spawn.rs:36-59`](../../../cli-source-references/ZENG3LD-gate4agent/src/acp/spawn.rs):

```rust
pub(crate) fn acp_command(tool: CliTool) -> AcpSpawnSpec {
  match tool {
    CliTool::Gemini    => AcpSpawnSpec { program: "gemini",   args: &["--experimental-acp"], … },
    CliTool::OpenCode  => AcpSpawnSpec { program: "opencode", args: &["acp"],                … },
    CliTool::ClaudeCode => AcpSpawnSpec { program: "npx",
        args: &["-y", "@agentclientprotocol/claude-agent-acp"], npm_tool: true },
    CliTool::Codex     => AcpSpawnSpec { program: "npx",
        args: &["@zed-industries/codex-acp"],                  npm_tool: true },
  }
}
```

Stdin stays open for the entire session
([`src/acp/spawn.rs:67-156`](../../../cli-source-references/ZENG3LD-gate4agent/src/acp/spawn.rs))
so that bidirectional JSON-RPC can run multi-turn without respawning. This
maps **directly** onto ADR-0013's `CliRunEvent` typed channel and removes
the bespoke per-CLI parsing entirely.

**Watchdog.** The reader loop in
[`src/pipe/session.rs:136-193`](../../../cli-source-references/ZENG3LD-gate4agent/src/pipe/session.rs)
has no silence timer — it polls `try_recv` with a 10 ms sleep and exits the
loop only when the child is no longer running, then synthesises a
`SessionEnd` event with `is_error = exit_code != 0` if the parser never
emitted one (Codex's documented quirk). This is the same shape as our
`MonitorProcessAsync`.

**Transferable patterns.**
- Three transports as parallel modules rather than mode flags. Caller
  picks one at session-create time.
- Per-CLI builder files (we already have these).
- Synthetic `SessionEnd` from `(exit_code, parser-emitted-end?)` so the
  outer loop always sees one terminator regardless of CLI quirks.
- Windows-specific `cmd /C` wrapping kept, args passed individually to
  avoid quoting issues — alternative to our ADR-0011 `.CMD → .exe`
  rewrite.

---

### 4. `awslabs/cli-agent-orchestrator` (Python, Apache-2.0)

**What it orchestrates.** Claude Code, Codex, GitHub Copilot CLI, Gemini CLI,
Q CLI, Kiro, OpenCode, Kimi — every CLI ships as its own
`providers/<name>.py` file. Spawn target is **always tmux**, never a direct
child of the orchestrator process.

**Repo at:** `c:/Projects/agent-taskboard-devspace/cli-source-references/awslabs-cli-agent-orchestrator`

**Spawn shape (Claude).** [`src/cli_agent_orchestrator/providers/claude_code.py:243-258`](../../../cli-source-references/awslabs-cli-agent-orchestrator/src/cli_agent_orchestrator/providers/claude_code.py):

```python
command = self._build_claude_command()      # → "unset CLAUDE…; claude --dangerously-skip-permissions …"
…
tmux_client.send_keys(self.session_name, self.window_name, command)
```

The CLI is launched **inside a tmux pane** that the orchestrator already
created. CAO never holds the agent's stdout pipe — tmux does. CAO observes
state by `tmux capture-pane`-ing the visible buffer and parsing it with
regex (`get_status` walks separator/spinner/idle markers).

**Three Windows-specific hardenings worth noting.**

a. **Nested-session detection avoidance.**
   [`providers/claude_code.py:138-149`](../../../cli-source-references/awslabs-cli-agent-orchestrator/src/cli_agent_orchestrator/providers/claude_code.py):

   ```python
   "unset $(env | sed -n 's/^\\(CLAUDE[A-Z_]*\\)=.*/\\1/p'
            | grep -v -E 'CLAUDE_CODE_USE_(BEDROCK|VERTEX|FOUNDRY)|CLAUDE_CODE_SKIP_…') 2>/dev/null"
   ```

   When the orchestrator itself runs inside Claude Code, the `CLAUDE*` env
   vars leak into spawned panes via tmux's global env. Claude detects them
   and refuses to start ("nested session"). CAO unsets them right before
   launch. **This is suspect C in our briefing**: env-var inheritance shape
   differs between our `dotnet run` (full env) and `dotnet test` (more
   restricted env). If our backend was ever started from inside a Claude /
   Codex / Gemini host (e.g. an agent is editing the code that spawns the
   agent), the same nested-session detection might be active.

b. **Bypass-permissions auto-accept.**
   [`providers/claude_code.py:152-178`](../../../cli-source-references/awslabs-cli-agent-orchestrator/src/cli_agent_orchestrator/providers/claude_code.py):
   sets `skipDangerousModePermissionPrompt: true` in `~/.claude/settings.json`
   before launching. Without this flag set, `--dangerously-skip-permissions`
   shows a confirmation dialog **on every launch** that blocks until
   answered. Our backend uses the flag too; if the user has ever seen this
   dialog on a fresh dev box, that is exactly the "first frame, then
   silence" symptom shape (the init frame would emit, and then the agent
   waits at the dialog forever).

c. **Gemini "trust this folder" trust-store seeding.**
   [`providers/gemini_cli.py:143-176`](../../../cli-source-references/awslabs-cli-agent-orchestrator/src/cli_agent_orchestrator/providers/gemini_cli.py):
   pre-writes `~/.gemini/trustedFolders.json` with `TRUST_PARENT` for the
   workspace tree because Gemini 0.40+ **blocks on an interactive trust
   dialog the first time it sees an unknown directory**. CAO documents
   this explicitly: *"without this bootstrap every gemini launch would
   hang at that prompt."* Same symptom shape as our hang.

**Gemini warm-up echo.** [`providers/gemini_cli.py:480-503`](../../../cli-source-references/awslabs-cli-agent-orchestrator/src/cli_agent_orchestrator/providers/gemini_cli.py)
sends `echo CAO_SHELL_READY` and waits for it to round-trip *before*
launching `gemini`, then sleeps 2 seconds, because Gemini's Ink TUI exits
silently in tmux sessions where the shell environment isn't fully loaded.
Another surfaced suspect-shape: **post-init silence may be the agent
emitting an init frame before its real init completes, then silently
crashing/exiting.**

**Watchdog.** Per-status polling on the tmux capture buffer with `time.sleep(1.0)`
plus per-state timeouts (init: 30 s for Claude, 240 s for Gemini due to
MCP-server-download time). Initialisation timeout produces a diagnostic
dump of the last 50 lines of the pane.

**Transferable patterns.**
- One-file-per-CLI providers with an `initialize()` / `get_status()` /
  `extract_last_message()` contract.
- Per-CLI tunable init timeouts (240 s for Gemini, not the universal
  60 s we use). **This alone might explain a fraction of our "watchdog
  killed it at 60-180 s" reports if Gemini is slow on first launch.**
- Pre-trust the workspace, pre-set permissions, unset nested-session env
  vars **before** the spawn instead of after seeing the symptom.

---

### 5. `sst/opencode` (TypeScript, MIT)

**What it orchestrates.** Its own coding-agent loop, plus a plugin slot
where `unixfox/opencode-claude-code-plugin` (above) attaches.

**Repo at:** `c:/Projects/agent-taskboard-devspace/cli-source-references/sst-opencode`

**Findings.** The opencode core does not orchestrate Claude/Gemini/Codex CLI
as subprocesses — it talks to provider HTTP APIs directly. The interesting
data point for us is the **plugin contract** that `unixfox`'s plugin
satisfies: opencode loads external providers dynamically and expects them
to expose AI SDK's `LanguageModelV2` interface. This is the interface that
hides the spawn-Claude-as-child-process mechanics, and it is the
architecturally cleanest seam we've seen for "a Claude Code subprocess
pretending to be an LLM provider."

**Native ACP support.** opencode ships an `acp` subcommand
([gate4agent ACP spec line 43](../../../cli-source-references/ZENG3LD-gate4agent/src/acp/spawn.rs)) and the
[official ACP docs](https://opencode.ai/docs/acp/) document it as a
production-supported transport. So if we adopted ACP, opencode is one of
the four targets *and* it works as both client and server.

---

### 6. `JeromySt/vscode-copilot-orchestrator` (TypeScript, MIT)

**What it orchestrates.** GitHub Copilot CLI agent mode, multiple parallel
background agents from VS Code.

**Repo at:** `c:/Projects/agent-taskboard-devspace/cli-source-references/JeromySt-vscode-copilot-orchestrator`

**Spawn shape.** Per the README + our search results: JSON-RPC 2.0 over
stdin/stdout to the Copilot CLI server, with `StdioTransport` reading
newline-delimited JSON-RPC frames; communication via IPC (named pipe,
nonce auth) when spawning multiple background panes. This is the same
shape as the official `github/copilot-sdk` already-cloned reference —
i.e. the JSON-RPC server lane is the supported way Copilot agents are
expected to be embedded.

**Why it matters for us.** Confirms that **Copilot's path is JSON-RPC,
not stream-json**, so an ACP adoption gives us Claude/Codex/Gemini parity,
and Copilot stays on its own JSON-RPC path. We don't have to find a
"Copilot ACP" binding — Copilot SDK *is* the typed protocol. ADR-0013
already reflects this.

---

### 7. `kingbootoshi/codex-orchestrator` (TypeScript, MIT) — supplementary

**What it orchestrates.** OpenAI Codex agents inside tmux sessions, designed
to be driven by Claude Code.

**Repo at:** `c:/Projects/agent-taskboard-devspace/cli-source-references/kingbootoshi-codex-orchestrator`

**Pattern.** Same as `awslabs/cli-agent-orchestrator` but smaller: tmux for
hosting, file-system tail of `~/.codex/sessions/*.jsonl` for state. The
on-disk session JSONL file is the load-bearing observation: Codex writes a
parallel transcript to disk that doesn't depend on stdout pipes. **For
stale-session continuation (ADR-0014) we already lean on this; cross-check
that the session-file path stays stable across CLI versions.**

---

### 8. `Aider-AI/aider` (Python, Apache-2.0) — supplementary

**What it orchestrates.** Its own LLM-loop (it calls model APIs / endpoints
directly via litellm). It does **not** spawn Claude/Gemini/Codex CLI as
subprocesses, so it is **not a direct reference** for our spawn problem.

**One pattern worth keeping.**
[`aider/coders/base_coder.py`](../../../cli-source-references/Aider-AI-aider/aider/coders/base_coder.py)
defines `max_reflections = 3` for "agent emits incomplete output, retry
with a clarifying nudge." We already cite this in ADR-0008
(`StuckLoopBudget`). Nothing new here.

---

## Patterns

### P1. Default-deny stdin

Every orchestrator that successfully spawns Claude as a child process and
doesn't hang either (a) sets stdio = ignore (Node's `'ignore'`,
`subprocess` `stdin=DEVNULL`), or (b) explicitly writes the prompt and
*then drops the handle to actual EOF before waiting on stdout*. The shared
property is "stdin reaches `EOF` before the child reads it", not just
"stdin is technically closed by the parent eventually."

We currently *write-then-close*. On Windows .NET, the close happens after
the read loops have already started, and the child process can already be
in a "reading" state when our close fires. The fix is to flip the default
case to **don't connect stdin at all** for `--print` jobs, and only
connect it when we genuinely have a stdin payload (Claude's prompt
piping).

### P2. JSON-RPC over stdio is the converged protocol

ACP (Zed), App Server (Codex), Copilot SDK (GitHub) — three independent
projects, one shape. Stream-json NDJSON is a per-CLI dialect; JSON-RPC is
the cross-CLI protocol. Every survey reference that handles all four CLIs
either uses it (gate4agent `acp/`) or wishes it did.

### P3. Persistent process per (cwd, model), not per turn

`unixfox/opencode-claude-code-plugin` and gate4agent ACP both keep the
agent process alive across turns and write subsequent prompts to the
already-open stdin. Today our app spawns a fresh process per turn (per
ADR-0011 "we accept the spawn cost"). This is fine for `--print` mode but
incompatible with ACP, which is multi-turn by design. **Do not flip this
silently** — it's a real architectural shift that interacts with our
sequential-per-project boundary (ADR-0001) and our session-id resume
contract (ADR-0014).

### P4. Hosted-in-terminal as fallback when pipes hate you

CAO uses tmux. hcom uses a real PTY for everything except Claude `-p`. The
common move is: when the CLI emits an interactive TUI that doesn't expose
a clean machine-readable mode, *put it inside a real terminal* and parse
what humans see. We already considered this for Copilot (we have
`PtySession`) and rejected it for Claude `-p` (Claude detects TTY stdin
and bails per ADR-0011). The pattern is not "PTY everything," it's
**"PTY when the CLI is interactive-first, pipes when the CLI has a
machine mode, and never mix the two."**

### P5. Pre-emptive trust-store / settings hardening

CAO does at least three of these on every launch:
- `~/.claude/settings.json` ← `skipDangerousModePermissionPrompt: true`
- `~/.gemini/trustedFolders.json` ← `TRUST_PARENT` for the workspace tree
- `unset CLAUDE*` env vars (except auth-mode flags) before launch

Each one prevents an interactive blocking dialog whose symptom is
indistinguishable from the post-init silence we see. **At least the
nested-session unset (suspect C) and the bypass-permissions settings flag
are zero-risk wins for us today.**

### P6. Per-CLI init timeout, not a universal one

CAO budgets 30 s for Claude, 240 s for Gemini (because of MCP server
download on first launch). Our base watchdog uses one budget. If a Gemini
run on a fresh box is the symptom, *our watchdog is killing a healthy
process that is still legitimately initialising MCP servers.*

### P7. Synthetic terminal events on exit-without-end

gate4agent, our codebase, and `unixfox`'s plugin all do the same thing on
process exit: if the parser never saw a terminal `result` / `SessionEnd`
event, synthesise one from `(exit_code, captured_lines)`. This is already
in `CliExecutionServiceBase.MonitorProcessAsync`. Pattern-match confirmed.

---

## Recommended next moves for agent-taskboard

Each move below names what we'd build, how it lands on the existing
`backend/Services/Cli/` architecture, and a rough cost estimate.

### R1. Fix stdin-handling per claude-code #771 — *immediate*

**What.** In `CliExecutionServiceBase.SpawnChildAsync`, when
`GetPromptStdinPayload` returns `null` (i.e. *we have no stdin payload to
write*), set `psi.RedirectStandardInput = false` so the child inherits no
parent stdin handle at all instead of an open-then-closed pipe. For the
Claude path that *does* have a payload, keep redirection but write the
payload synchronously *before* `psi.RedirectStandardOutput = true` reads
take effect — i.e. keep the spawn order strict: spawn → write stdin → flush
→ close → only then start `ReadStreamAsync` tasks.

**Mapping.** Touches `CliExecutionServiceBase.cs:226-272` (the spawn
block). Per-CLI overrides do not change. Behind a feature flag
`CliRunner:DisconnectStdinByDefault=true` so we can A/B against the live
hang.

**Cost.** ~50 LOC + 1 deterministic test in `CliWatchdogIntegrationTests`
that asserts stdin disconnect-by-default + 1 live test in
`CliSpawnIntegrationTests` that re-runs the original failing case under
ASP.NET hosting (use `WebApplicationFactory` to actually reproduce the
host shape that `dotnet test` doesn't normally exercise).

**Why first.** Smallest-radius change that addresses the
single-best-evidence root cause. Independent of any larger architectural
move.

### R2. Pre-emptive trust-store hardening on backend boot — *immediate*

**What.** On `TaskRunnerService.ExecuteAsync` boot, idempotently:
- Ensure `~/.claude/settings.json` has `skipDangerousModePermissionPrompt: true`.
- Ensure `~/.gemini/trustedFolders.json` has `TRUST_PARENT` for our
  TaskRepository root.
- Unset `CLAUDE*` env vars except `CLAUDE_CODE_USE_*` /
  `CLAUDE_CODE_SKIP_*_AUTH` from `psi.Environment` for every Claude
  spawn.

**Mapping.** New `backend/Services/Cli/CliEnvironmentHardening.cs` with
three pure functions; called from `TaskRunnerService.ExecuteAsync` once
on startup and from each adapter's `BuildStartInfo` for the env-var
filter. Pattern parity with CAO's
[`providers/claude_code.py:138-178`](../../../cli-source-references/awslabs-cli-agent-orchestrator/src/cli_agent_orchestrator/providers/claude_code.py).

**Cost.** ~80 LOC + 6 unit tests. Zero behavioural risk: the operations
are idempotent, no-op when the file/key already has the right value.

### R3. Per-CLI init silence budgets in `PhaseAwareWatchdog` — *near-term*

**What.** Extend ADR-0013's phase enum (`Spawning` →
`SessionInitializing` → … ) so each phase has its own per-CLI budget:
Claude `SessionInitializing = 30 s`, Gemini = 180 s, Codex = 60 s,
Copilot = 60 s. Match CAO's empirically-tuned numbers.

**Mapping.** Extend `RunPhaseTransitions.cs`. The watchdog already lives
on the runner; adding a `(cli, phase) → TimeSpan` lookup is mechanical.

**Cost.** ~120 LOC + 1 deterministic fake-CLI test per phase per CLI.
Roughly half a day. Replaces the silence-only watchdog ADR-0013 already
flags as the fallback path.

### R4. ACP transport behind a feature flag, Gemini first — *medium*

**What.** Add a third spawn transport alongside the existing pipe path:
`AcpCliService` that talks JSON-RPC 2.0 over stdio to one of:

| CLI    | Spawn target                                           |
|--------|--------------------------------------------------------|
| Gemini | `gemini --experimental-acp` (native)                   |
| Codex  | `npx @zed-industries/codex-acp` (Zed-maintained shim)  |
| Claude | `npx -y @agentclientprotocol/claude-agent-acp` (shim)  |
| OpenCode | `opencode acp` (native) — out of scope today        |

The runner picks transport by `appsettings` per-CLI flag, e.g.
`Cli:Gemini:Transport=Acp`. `CliRunEvent` (ADR-0013) becomes the canonical
internal type; ACP's events map directly onto it (`session/update` →
`OutputDelta`, `session/request_permission` → `ApprovalRequested`, etc.).

**Mapping.** New `backend/Services/Cli/Acp/` with `AcpClient.cs`,
`AcpJsonRpc.cs`, `AcpEventMapper.cs`. New
`backend/Services/Cli/AcpCliService.cs` extends `CliExecutionServiceBase`
and overrides `SpawnChildAsync` + `MapLineToRunEvents`. The existing
per-CLI services (`ClaudeCliService` etc.) stay; only the transport
override differs.

**Why Gemini first.** It has native `--experimental-acp` (no npx shim
hop), our existing Gemini stream-json path is the lowest-traffic of the
four, and ACP would let us **delete** the bespoke
`GeminiEventAdapter.cs` line-by-line parser entirely on success.

**Cost.** Realistic estimate ~3 days of focused work for the Gemini
transport alone (JSON-RPC client infra is reusable for the other three).
Adds an npm dependency for the Claude / Codex shims when we extend.

**Risk.** ACP shims for Claude/Codex add a `npx` boot cost (~2-3 s) per
spawn the first time; pin them locally. The shims are maintained by
third parties (Zed, agentclientprotocol.com); validate version pinning
in `package.json` parity with Anthropic's release cadence.

### R5. Bidirectional stream-json mode for Claude follow-up turns — *medium*

**What.** Adopt the `unixfox` pattern: pass
`--input-format stream-json --output-format stream-json --verbose` and
keep the process alive across turns, writing each follow-up to stdin as
a JSON message. Eliminates the per-turn spawn cost (~1 s) ADR-0011
explicitly accepted, and aligns Claude's transport with where ACP wants
us to land.

**Mapping.** A second `ClaudeCliService` mode (`Cli:Claude:LongLived=true`).
Major change: per-(project, model) process pool. Interacts with ADR-0001
(sequential-per-project — fine, one process per project at a time) and
ADR-0014 (stale-session reliability — better, the live process *is* the
session).

**Cost.** ~5 days. New tests for "follow-up turn writes to stdin instead
of respawning," graceful idle eviction, project-runner-level coordination.

**Why "medium" not "immediate."** Behaviour-changing for Claude. Should
land *after* R1 (stdin fix) and R3 (per-phase budgets) so the regressions
have a clean baseline.

---

## What we should NOT do

Each entry names a pattern that *looks* attractive in the survey but does
not fit our product boundaries.

### N1. tmux / screen-scraping — incompatible with Windows-native ADR-0011/0012

CAO and kingbootoshi/codex-orchestrator are tmux-only. `tmux` doesn't run
on Windows except inside WSL2. Adopting a tmux-host architecture means
either (a) requiring WSL2 (ADR-0011 explicitly says no), or (b) writing
our own pseudo-tmux on Windows ConPTY. Both are massive scope creep for
zero subscription-billing wins.

### N2. PTY-everything — already proven incompatible with `claude -p`

ADR-0011 records that we tried PTY for Claude `-p` and it failed because
Claude exits with code 1 when it sees `stdin = TTY`. The PTY hook in
`SpawnChildAsync` is kept for genuinely-interactive CLIs (Copilot's
`PtySession` is the existing example). Don't widen its application
without a fresh probe per CLI.

### N3. Direct Anthropic / OpenAI API calls — violates ADR-0006 / 0012

`unixfox/opencode-claude-code-plugin` doesn't go this way (it spawns the
CLI), but other plugins in the opencode ecosystem do. Several "Claude
SDK" packages on npm/PyPI talk directly to the provider HTTP API. Both
ADR-0006 and ADR-0012 are explicit: subscriptions are the budget, not
API keys. Even in the orchestrator path. **Especially** there.

### N4. Multi-agent fan-out / branch-per-task / worktrees — violates ADR-0001

The hcom message bus, kingbootoshi/codex-orchestrator's parallel-agents
shape, and `josstei/maestro-orchestrate`'s "39 specialists, parallel
subagents" pattern are all interesting but explicitly off-limits per
ADR-0001's "sequential per project, parallel across projects" boundary.
Mention them in chat history if a user asks; never propose them as a
roadmap item without a fresh ADR.

### N5. SQLite-as-message-bus — solves a problem we don't have

hcom uses SQLite for inter-agent messaging. Our agents don't message each
other; the orchestrator decides on the user's behalf when an agent emits
`[[TASK_NEEDS_INPUT:...]]`. We already have the right shape (file-on-disk
`orchestrator.jsonl` + per-job `pending-intent.json`). Adopting SQLite
adds a binary format we don't need.

### N6. Long-lived global daemon process per CLI — interacts badly with reaper

If we adopt R5 (long-lived Claude per-project), the existing orphan
reaper (`ReapOrphans` in `CliExecutionServiceBase`) needs to know which
PIDs are *intentionally* long-lived vs. left-over from a backend crash.
Solvable, but **not free**. The simpler alternative — kill-on-idle after
N minutes — should be the default; full "daemon" semantics are out of
scope until we measure that the spawn cost is actually a UX problem.

---

## Open questions worth a probe (next iteration)

1. **Reproduce claude-code #771 from a `WebApplicationFactory` host.** Our
   existing `CliSpawnIntegrationTests` runs under the `dotnet test`
   process, which is a different stdin shape than ASP.NET hosting. Wrap
   the Claude probe in `WebApplicationFactory<Program>.CreateClient()`
   and re-run. If that reproduces the hang, we've confirmed suspect A
   *and* gained a deterministic test for it.
2. **Add a probe for "Claude spawned while another Claude is alive in the
   same `~/.claude/projects/<encoded-cwd>`"** — suspect B from the
   briefing. We have not directly tested this; the survey didn't surface
   evidence either way.
3. **Diff the env vars between `dotnet test` Claude spawn and `dotnet run`
   Claude spawn.** Suspect C from the briefing. CAO's `unset CLAUDE*`
   pattern is a strong hint that env-var leakage matters here.
4. **Check Anthropic's cap on concurrent stream-json sessions per
   subscription.** Suspect D. If the `--output-format stream-json` lane
   is rate-limited per account in a way `--output-format text` is not,
   that would explain why our test process (one Claude at a time) works
   and our backend (frequently runs Claude orchestrator + Claude task at
   the same time) hangs.

---

## References

### Primary code references (already on disk)

- **gate4agent** Pipe transport:
  [`src/pipe/process.rs`](../../../cli-source-references/ZENG3LD-gate4agent/src/pipe/process.rs),
  per-CLI builders in
  [`src/pipe/cli/`](../../../cli-source-references/ZENG3LD-gate4agent/src/pipe/cli/),
  ACP transport in
  [`src/acp/spawn.rs`](../../../cli-source-references/ZENG3LD-gate4agent/src/acp/spawn.rs).
- **awslabs/cli-agent-orchestrator** providers in
  [`src/cli_agent_orchestrator/providers/`](../../../cli-source-references/awslabs-cli-agent-orchestrator/src/cli_agent_orchestrator/providers/),
  notably `claude_code.py` and `gemini_cli.py`; tmux client in
  [`clients/tmux.py`](../../../cli-source-references/awslabs-cli-agent-orchestrator/src/cli_agent_orchestrator/clients/tmux.py).
- **unixfox/opencode-claude-code-plugin** spawn + session reuse in
  [`src/session-manager.ts`](../../../cli-source-references/unixfox-opencode-claude-code-plugin/src/session-manager.ts)
  and stream parser in
  [`src/claude-code-language-model.ts`](../../../cli-source-references/unixfox-opencode-claude-code-plugin/src/claude-code-language-model.ts).
- **hcom** launcher taxonomy in
  [`src/launcher.rs`](../../../cli-source-references/aannoo-hcom/src/launcher.rs);
  PTY in
  [`src/pty/mod.rs`](../../../cli-source-references/aannoo-hcom/src/pty/mod.rs);
  Gemini headless rejection in
  [`src/tools/gemini_args.rs`](../../../cli-source-references/aannoo-hcom/src/tools/gemini_args.rs).
- **Codex App Server protocol** schema files at
  `c:/Projects/agent-taskboard-devspace/cli-source-references/openai-codex/codex-rs/app-server-protocol/src/`.

### Per-repo NOTES.md (companion to this survey)

Each cloned reference now has a `NOTES.md` at its root summarising
why we cloned it, the most useful files inside, what transfers,
what's load-bearing-and-not-to-copy, and cross-references back here:

- [`aannoo-hcom/NOTES.md`](../../../cli-source-references/aannoo-hcom/NOTES.md)
- [`Aider-AI-aider/NOTES.md`](../../../cli-source-references/Aider-AI-aider/NOTES.md)
- [`anthropics-claude-code/NOTES.md`](../../../cli-source-references/anthropics-claude-code/NOTES.md)
- [`awslabs-cli-agent-orchestrator/NOTES.md`](../../../cli-source-references/awslabs-cli-agent-orchestrator/NOTES.md)
- [`github-copilot-cli/NOTES.md`](../../../cli-source-references/github-copilot-cli/NOTES.md)
- [`github-copilot-sdk/NOTES.md`](../../../cli-source-references/github-copilot-sdk/NOTES.md)
- [`hoangsonww-AI-Agents-Orchestrator/NOTES.md`](../../../cli-source-references/hoangsonww-AI-Agents-Orchestrator/NOTES.md)
- [`JeromySt-vscode-copilot-orchestrator/NOTES.md`](../../../cli-source-references/JeromySt-vscode-copilot-orchestrator/NOTES.md)
- [`kingbootoshi-codex-orchestrator/NOTES.md`](../../../cli-source-references/kingbootoshi-codex-orchestrator/NOTES.md)
- [`lucad87-gemini-orchestrator/NOTES.md`](../../../cli-source-references/lucad87-gemini-orchestrator/NOTES.md)
- [`microsoft-vscode-copilot-chat/NOTES.md`](../../../cli-source-references/microsoft-vscode-copilot-chat/NOTES.md)
- [`openai-codex/NOTES.md`](../../../cli-source-references/openai-codex/NOTES.md)
- [`sst-opencode/NOTES.md`](../../../cli-source-references/sst-opencode/NOTES.md)
- [`unixfox-opencode-claude-code-plugin/NOTES.md`](../../../cli-source-references/unixfox-opencode-claude-code-plugin/NOTES.md)
- [`ZENG3LD-gate4agent/NOTES.md`](../../../cli-source-references/ZENG3LD-gate4agent/NOTES.md)

### External

- **Issue claude-code#771** — *the* upstream evidence for the post-init
  silence symptom and its stdin-handling fix.
- **Agent Client Protocol** — open standard, Zed; spec at
  https://agentclientprotocol.com/get-started/introduction.
- **Anthropic April 23 postmortem** — context for ADR-0014; not
  duplicated here.
