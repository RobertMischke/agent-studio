# WSL2 vs Windows-native vs Linux/Mac — decision document (2026-05)

**Status.** Decision-candidate. May become **ADR-0015** if accepted.
Read in the same sitting as
[`cli-orchestration-survey-2026-05.md`](./cli-orchestration-survey-2026-05.md)
(per-repo evidence) and
[`path-forward-plan-2026-05.md`](./path-forward-plan-2026-05.md) (the
synthesis). This document is the second of those three load-bearing
companions; the survey lives in the first.

The user named this axis explicitly:

> *"Wir nehmen WSL 2, damit es dann leichter ist. Aber Windows wäre
>  schon… Es ist halt nochmal eine andere Kapazitätsstufe."*

That tension is the subject of this document. The recommendation,
named up front so the rest is supporting evidence rather than
suspense:

**Recommendation:** **Stay Windows-native for the dev seat;
keep WSL2 a documented alternative for users who prefer it; do NOT
require WSL2.** Rationale builds across the rest of the document; the
TL;DR is in § 7.

---

## 1. The empirical landscape today

### 1.1 What we observe

The original symptom — *Claude emits its first stream-json frame, then
60-180 s of silence until our watchdog fires* — has these properties:

| Property                                    | Value                                                |
| ------------------------------------------- | ---------------------------------------------------- |
| Reproduces under                            | ASP.NET-hosted backend (`dotnet run`)                |
| Does NOT reproduce under                    | `dotnet test` child-spawn, direct shell invocation   |
| Reproduces inside `WebApplicationFactory`?  | **Not yet probed**, see survey § "Open questions" #1 |
| OS                                          | Windows 11 + Node 20 + .NET 8                        |
| CLI                                         | Claude Code (`claude` via npm `.CMD` shim)           |
| Per ADR-0011                                | `.CMD → .exe` rewrite is mitigation, not proven fix  |

Survey § "Direct upstream evidence" elevated the
[`anthropics/claude-code#771`](https://github.com/anthropics/claude-code/issues/771)
issue — *"Claude Code can't be spawned from node.js, but can be from
python"* — from hypothesis to documented bug class. The Python
fix is `subprocess.run(..., capture_output=True)` (which sets
`stdin = DEVNULL`); the Node fix is `stdio: ['ignore', 'pipe', 'pipe']`.
The .NET equivalent is **non-trivial because `Process` has no
`'ignore'` option**; the closest is `RedirectStandardInput = false`
*combined with* the parent process having no controlling TTY.

### 1.2 Cross-platform vs. Windows-specific failure modes

A clean separation, because the two axes argue differently:

**Cross-platform (would NOT be solved by switching to WSL2):**

- Node `process.stdout` block-buffers when piped (vs line-buffered to
  TTY). Affects Claude / Gemini stream-json on every OS. The fix is
  protocol-level (newline-framed JSON-RPC / NDJSON we already parse
  line-by-line), not OS-level.
- Anthropic API throttling under simultaneous stream-json sessions
  (suspect D from the briefing). Same on every OS.
- Agent session-DB locks (`~/.claude/projects/<encoded-cwd>/*.jsonl`
  contention when two Claudes share a cwd) — same on every OS;
  filesystem locking semantics differ in the *flavour* of the failure
  but not in whether it can happen.
- The `--dangerously-skip-permissions` confirmation dialog and
  Gemini's "trust this folder" dialog (survey § P5, R2). Same on
  every OS.
- CLI version drift breaking flag stability (survey
  `JeromySt-vscode-copilot-orchestrator/NOTES.md` documenting
  `--max-turns` removal in CLI v1.0.31). Same on every OS.

**Windows-specific (would be either solved or sidestepped by WSL2):**

- The `.CMD` shim wrapping `claude.exe`: npm on Windows wraps
  `bin/claude.exe` (or `node bin/claude.js`) in a `claude.CMD`
  launcher because Windows shells need `.CMD` extensions for
  `Process.Start("claude")` to find the binary. ADR-0011 already
  rewrites this to the underlying `.exe`. On WSL2 / Linux / Mac the
  npm install lays out `bin/claude` as a real executable shebang
  script, no `.CMD` involved.
- ConPTY vs. winpty quirks for any future PTY adoption. WSL2 has
  Linux PTY (`/dev/ptmx`, openpty), well-trodden. ConPTY is newer
  (Windows 10 1809+) and has known quirks with terminal-resize and
  stdin-as-TTY-detection that gate4agent's portable-pty crate hides
  behind `cfg!(windows)` branches.
- File-handle inheritance across `Process.Start` on Windows is
  *coarser* than POSIX: `bInheritHandles = TRUE` inherits *all*
  inheritable handles, which on a hosted ASP.NET process can include
  the parent's stdin (a console handle pointing at the original TTY)
  unless we explicitly set `STARTUPINFOEX` with a curated handle
  list. .NET's `Process` API does *not* expose
  `STARTUPINFOEX.lpAttributeList`; closing it requires P/Invoke. On
  POSIX, `posix_spawn` with `FILE_ACTIONS_CLOSE` is the standard
  primitive. **This is the strongest Windows-specific suspect we
  have for the post-init silence symptom.**
- `cmd /C` argument-quoting weirdness for prompts containing
  `& | < > ^` (gate4agent works around this by passing args as
  separate `Command` elements, ADR-0011 went the other way and
  removed `cmd.exe` entirely from the chain).
- Watch-path detection (FileSystemWatcher on Windows ≠ inotify on
  Linux ≠ FSEvents on Mac); not currently a hang cause, but a
  source of subtle "I changed a file but the watcher didn't fire"
  bugs.

The *Windows-specific* set is non-empty and operationally real. The
*cross-platform* set is also non-empty — and would still bite us on
WSL2.

---

## 2. What WSL2 actually changes

WSL2 is not "Linux with Windows file paths." It is a real Linux
kernel inside a Hyper-V utility VM, with Windows-side bridges for
filesystem and networking. The bridges have load-bearing semantics.

### 2.1 What gets better

| Aspect                                | On Windows                                                      | On WSL2                                                            |
| ------------------------------------- | --------------------------------------------------------------- | ------------------------------------------------------------------ |
| `claude.CMD` shim                     | Required, hence ADR-0011 rewrite                                | Not needed; `bin/claude` is a real shebang script                  |
| `fork()` availability                 | None; `Process.Start` always uses `CreateProcess`               | Available; `posix_spawn` and friends                               |
| `/dev/ptmx` PTY                       | None (ConPTY exists but different semantics)                    | Available; `openpty(3)` works as documented                        |
| `posix_spawn` w/ FILE_ACTIONS_CLOSE   | None                                                            | Standard; CLOEXEC works                                            |
| Stdin handle inheritance              | Coarse (`bInheritHandles=TRUE`) — implicated suspect            | Fine-grained per-fd                                                |
| tmux / `script` / `tail -f`           | Not native                                                      | Standard                                                           |
| Linux variants of CLIs                | npm-via-`.CMD`                                                  | Native Linux binaries (smaller surface, no shim)                   |
| File watching                         | FileSystemWatcher (subtle async drops on rapid changes)         | inotify (well-trodden)                                             |
| Terminal env (`TERM`, `LANG`, `LC_*`) | Windows console; explicit env injection needed                  | Native Linux env; UTF-8 default                                    |

### 2.2 What gets worse, or stays the same

| Aspect                                | On Windows native                          | On WSL2                                                                                |
| ------------------------------------- | ------------------------------------------ | -------------------------------------------------------------------------------------- |
| GitHub Copilot CLI                    | Native binary                              | Native Linux binary (separate install path)                                            |
| Claude Code CLI                       | npm package, `.CMD` shim                   | npm package, no shim (real shebang)                                                    |
| Codex CLI                             | npm package + Rust binary                  | npm package + Rust binary, Linux variant                                               |
| Gemini CLI                            | npm package, Node TUI (Ink)                | Same                                                                                   |
| Auth flows                            | Windows browser opens for OAuth            | **WSL2 has known clunkiness here** — `wslview` / `xdg-open` don't always work          |
| File paths                            | `C:\Projects\...`                          | `/mnt/c/Projects/...` (slow!) or `\\wsl.localhost\<distro>\...`                        |
| File-system performance               | Native NTFS                                | `/mnt/c/...` is **5–10× slower** than `/home/<user>/...`                               |
| .NET tooling (`dotnet`, `dotnet test`) | First-class                                | Works, but interop with Windows-installed tools is limited                             |
| Visual Studio                         | First-class                                | Cannot edit files directly in WSL2 from VS (some workarounds)                          |
| VS Code                               | First-class                                | First-class via Remote-WSL extension                                                   |
| JetBrains Rider                       | First-class                                | Works, but slower indexing on `\\wsl.localhost`                                        |
| Network: reach localhost from WSL2 → Windows app | N/A | `host.docker.internal` or `$(hostname).local`; subtle and version-dependent |
| Network: reach Windows from WSL2-hosted backend | N/A | Same plus firewall config ("WSL2 can't reach Windows host" is a forum FAQ) |
| DNS                                   | Native                                     | Generated `/etc/resolv.conf`; corporate VPNs can break it                              |
| Memory accounting                     | Native                                     | WSL2 has its own VM with `.wslconfig` memory limits; users hit it                      |
| Update cadence                        | Windows Update                             | `wsl --update` separate; sometimes ships breaking changes                              |
| Onboarding cost                       | "Install Visual Studio + Node + dotnet"    | "Install WSL2 + Ubuntu + Node + dotnet + ensure systemd + ensure DNS + ensure browser-redirect-back-to-Windows-host works"|
| **`/mnt/c/...` on WSL2 + watching**   | -                                          | **Broken / unreliable** for most file-system watchers (CRLF, inotify-doesn't-fire-on-Windows-side-changes)             |

### 2.3 The two facts that dominate

**Fact A: WSL2 does not eliminate the cross-platform symptom set.** Node
block-buffering, Anthropic API throttling, agent-session-file
contention, version drift, trust-dialog blocking — all still apply
on WSL2.

**Fact B: WSL2 *does* eliminate the Windows-specific symptom set,
but only the Windows-specific subset of our suspects.** Stdin
inheritance via `bInheritHandles=TRUE`, the `.CMD` shim, ConPTY
quirks, `cmd /C` quoting — these go away.

So the WSL2 question reduces to: **how much of our remaining hang
budget is Windows-specific?**

If the answer is "most of it," WSL2 is the cheap fix. If the answer
is "some, but the rest is cross-platform," WSL2 buys partial
relief at substantial cost.

The honest answer today, given the evidence, is **"some" — possibly
the dominant suspect (stdin handle inheritance) but not provably
all of it.** Survey § "Open questions" #1 (reproduce
claude-code#771 from a `WebApplicationFactory` host) is the probe
that would let us know definitively before committing.

---

## 3. Cost of moving to WSL2 (developer-onboarding lens)

### 3.1 Onboarding burden

A new contributor on Windows today does:

```
1. Install Visual Studio 2022 (or Rider).
2. Install Node 20, .NET 8 SDK.
3. `npm install -g @anthropic-ai/claude-code @openai/codex @google/gemini-cli`
4. `gh copilot install` (or whatever the current install incantation is).
5. Clone repo.  `dotnet run` works.
```

Five steps, all of which the user already has muscle memory for.

A new contributor on Windows + required-WSL2 does:

```
1. Enable WSL2 Windows feature, set default version 2.
2. Install Ubuntu 22.04 (or whatever distro).
3. Configure `~/.wslconfig` for memory (otherwise WSL2 grows to half RAM).
4. Configure `/etc/wsl.conf` for systemd (needed by some CLI auth flows).
5. Install Node 20, .NET 8 SDK *inside Ubuntu* (separate from Windows installs).
6. `npm install -g …` *inside Ubuntu*.
7. Configure browser-redirect for Claude OAuth.  (`wslview` etc.)
8. Clone repo into `~/projects/agent-taskboard` (NOT `/mnt/c/...`, for fs perf).
9. Configure VS Code Remote-WSL extension.  Or accept slower JetBrains.
10. `dotnet run` *inside Ubuntu*.
```

This is a real cost. Some of those steps fail intermittently in
ways that are hard to debug ("why is `wsl --install` stuck?",
"why doesn't Ubuntu have systemd?", "why is npm slow?"). The
project lead has the experience to navigate this; future
contributors and AI agents (which see only what we document) do
not by default.

### 3.2 Editor / IDE cost

The user's stated tools are Visual Studio + JetBrains Rider for the
backend, VS Code for the frontend. Visual Studio **does not have a
first-class Remote-WSL story**; debugging .NET in WSL2 from VS is
possible but clunky. Rider has a remote-development feature that
works, but indexing `\\wsl.localhost\Ubuntu\home\<user>\projects\...`
is slow.

VS Code's Remote-WSL is first-class and is the path most WSL2-first
.NET developers take. But this would push us toward "VS Code as the
primary backend editor," which is a workflow change.

### 3.3 CI implications

CI today runs on Windows + Linux GitHub-hosted runners. Adding "must
run on Linux" is fine for a *Linux runner*. Adding "Windows users
must use WSL2" is a contributor-onboarding cost, not a CI cost.

If we *required* WSL2, CI could simplify (drop the Windows runner
matrix). If we keep both as supported, CI stays as-is.

### 3.4 Watch-path detection inside WSL2

Survey § "open questions" doesn't cover this but the dev experience
does: file watchers running *inside WSL2* on files stored on the
*Windows side* (`/mnt/c/...`) **do not fire reliably for changes
made on the Windows side.** This is a documented Microsoft
limitation. If we required WSL2 and *also* allowed checkouts under
`/mnt/c/`, our `FileSystemWatcher`-equivalent in .NET (or our
`fs.watch` in Node) would silently miss changes.

The fix is "always check out into `~/projects/...` *inside* WSL2."
Which means: the project source lives inside the VM, accessed from
Windows-side editors via `\\wsl.localhost\<distro>\...`. Which
means: Visual Studio / Rider performance hits.

### 3.5 Tools that no longer "just work"

A non-exhaustive list of things that *do* work on Windows native and
*don't* (or work clunkily) inside WSL2:

- Authenticator apps that open the Windows default browser
  (Claude OAuth, Codex login, Copilot device-code flow). All
  workable but each requires `wslview` / `wslu` configuration.
- Windows-installed git credential managers. WSL2 has its own; the
  config is separate.
- Process tooling: `Get-Process` PowerShell vs `ps`/`pgrep`. Our
  diagnostic scripts in `Script/` lean on PowerShell.
- OS-level file dialogs (when the user attaches a screenshot via
  the frontend → backend → CLI path; not currently in scope but
  drift-prone).

---

## 4. Counterargument — "fuck Windows, just go WSL2"

The strongest version of this argument:

> Most AI infra projects are Linux-first. `aannoo/hcom` is
> Unix-only; `kingbootoshi/codex-orchestrator` assumes Linux or
> WSL2; `Aider-AI/aider` is Python and runs everywhere but optimised
> on Linux; `awslabs/cli-agent-orchestrator` requires tmux (so
> requires WSL2 on Windows). The common path among *production-
> quality* CLI orchestrators is "we don't pretend Windows is
> first-class." Why are we trying to?

This is a legitimate argument. We acknowledge it.

The counter-counterargument — why we are nonetheless not
adopting it:

1. **The user is a Windows user with Visual Studio + Rider workflows.**
   The product targets Windows-native development as a first-class
   constraint per ADR-0011 ("the product targets Windows-native
   development"). This is not a rationalised post-hoc constraint;
   it is the user's stated working environment. Required-WSL2 means
   "switch your IDE."
2. **The Windows-native CLI lane is *real*.** Copilot CLI ships
   first-class on Windows; Codex ships first-class on Windows;
   Claude Code ships on Windows (with the npm-shim quirk we already
   handle); Gemini CLI ships on Windows. None of the four CLIs are
   Linux-only. The "WSL2-first AI infra" pattern is true for
   *orchestrators*, not for the CLIs themselves. We are an
   orchestrator that runs on the same OS as the CLIs it drives;
   forcing a layer between us is non-obvious.
3. **The hang is *not yet proven Windows-specific.*** Survey § R1
   (stdin handling fix) is platform-agnostic; the claude-code#771
   class affects Node-on-Linux too. If we move to WSL2 *and* don't
   fix stdin handling, we'd reproduce a similar hang. The fix is
   the same shape on both platforms.
4. **The third-option (§ 5) gives us most of the WSL2 benefit without
   the contributor-onboarding cost.**
5. **Forcing WSL2 would make agent-taskboard incompatible with one
   of its target users — itself.** When agent-taskboard's
   orchestrator is run *by* a Claude Code session (the dogfooding
   path), Claude is running on Windows-native today. A WSL2
   requirement would force the dogfooding case to either run
   Claude inside WSL2 (different auth, separate session DB) or
   accept that the orchestrator can't run from there at all.

The counterargument has merit but is outweighed by the user's
stated constraints and the third-option availability.

---

## 5. The third option — Windows-native + Linux/Mac with platform-conditional code paths

This is what we already do, but it deserves a name and a forward
investment plan.

**Pattern.** Use `OperatingSystem.IsWindows()` /
`OperatingSystem.IsLinux()` / `OperatingSystem.IsMacOS()` for
small, well-bounded platform-specific code paths in the .NET
backend. Keep the protocol-level logic (CliRunEvent emission,
phase-aware watchdog, stale-session reconciliation) entirely
platform-agnostic.

**Concrete platform-conditional code we already have or should
add:**

| Concern                                   | Windows                                   | Linux/Mac                                | Where                                  |
| ----------------------------------------- | ----------------------------------------- | ---------------------------------------- | -------------------------------------- |
| `claude.CMD` → `claude.exe` rewrite       | Active                                    | No-op (binary is the bin)                | `ClaudeCliService.ResolveCmdShimToExe` |
| Process kill                              | `taskkill /F /T /PID <pid>`               | `kill -TERM <pgid>` then `kill -KILL`    | `CliExecutionServiceBase.KillAsync`    |
| Default stdin behaviour                   | `RedirectStandardInput = false` + close   | Same; close + ignore                     | `CliExecutionServiceBase.SpawnChildAsync` (planned R1) |
| `STARTUPINFOEX.lpAttributeList`           | Optional P/Invoke for handle-list curate  | N/A                                      | New: `WindowsHandleScrub.cs` (proposed) |
| Trust-store seed paths                    | `%USERPROFILE%\.claude\settings.json`     | `~/.claude/settings.json`                | `CliEnvironmentHardening` (planned R2) |
| File watcher                              | `FileSystemWatcher`                       | inotify via `FileSystemWatcher`          | Already abstracted                     |

**Cost.** ~5-15% of CLI service code is platform-conditional. Already
the case today. Maintenance overhead is real but bounded — and
testable, because we can run CI on both Windows and Linux runners.

**Benefit.** Users pick their preferred environment. Dogfooding
works. The CLI surface stays first-class everywhere. WSL2 is *a
supported alternative* for users who prefer it (we don't actively
break it; we just don't require it).

**The deal we make.** If a Windows-specific suspect (handle inheritance,
ConPTY weirdness) bites us, we *do* the P/Invoke fix (or document
"set `RUN_BACKEND_VIA_WSL2=1` and use the WSL2 path"). We accept
that the .NET `Process` API has gaps, and we either fill them or
escape to platform-native code where needed.

---

## 6. The honest unknowns

These remain open and should not be papered over by either decision:

1. **Does claude-code#771 reproduce inside `WebApplicationFactory`
   on Windows?** If yes → suspect A confirmed → R1 (stdin fix)
   probably solves it on both Windows and WSL2. If no → we have a
   *different* trigger and the WSL2 axis matters more.
2. **Is the post-init silence stdin-related at all, or is it
   `bInheritHandles` propagating *some other* handle that the child
   blocks on?** We have not yet enumerated which handles a hosted
   ASP.NET process holds open at child-spawn time. If it's a console
   handle from `dotnet run`, R1 fixes it. If it's something else
   (an OutputDebugString handle, a named pipe to the IIS host
   metabase), we'd need a different fix and WSL2 would solve it
   incidentally.
3. **Does Anthropic rate-limit stream-json sessions per account?**
   Suspect D. WSL2 doesn't change this either way.
4. **What does the user actually want as the dev experience?**
   Their statement is conflicted ("WSL2 wäre einfacher, aber
   Windows wäre schon Kapazitätsstufe"). The recommendation here
   reflects the working environment they've built; if that
   changes, this decision should be revisited.

---

## 7. Recommendation (TL;DR)

**Go Windows-native. Fix the platform-agnostic suspects first.
Keep WSL2 as a documented alternative; don't require it.**

Phasing:

1. **Now:** R1 (stdin fix — survey § R1, claude-code#771) +
   R2 (env-hardening — survey § R2). Both are platform-agnostic;
   neither requires the WSL2 decision.
2. **Probe:** Run claude-code#771 inside `WebApplicationFactory`
   on Windows native (survey § "Open questions" #1). Either it
   reproduces (→ R1 fixes both Windows native and WSL2) or it
   doesn't (→ we have a Windows-specific trigger and we need the
   STARTUPINFOEX P/Invoke; budget 1-2 days).
3. **If a Windows-specific trigger remains after R1:** add
   `WindowsHandleScrub.cs` with P/Invoke to `STARTUPINFOEX` +
   `UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_HANDLE_LIST)`
   so we explicitly do *not* inherit any non-CLI-stdio handles.
   This is the .NET equivalent of Node's `'ignore'` /
   Python's `DEVNULL` / POSIX's `posix_spawn_file_actions_addclose`.
4. **Accepted alternative path for users who prefer it:** document
   how to run the backend inside WSL2; provide a
   `docs/wsl2-development.md` (not yet written) with the
   onboarding checklist; make CI on Linux runner the canonical
   "this works on Linux too" check. Do not flip the default.

**Effort estimate**

| Step                              | Days     | Risk |
| --------------------------------- | -------- | ---- |
| R1 (stdin fix)                    | 0.5–1    | Low  |
| R2 (env hardening)                | 0.5–1    | Low  |
| `WebApplicationFactory` probe     | 0.5      | Low  |
| `WindowsHandleScrub` P/Invoke     | 1–2      | Med  |
| `docs/wsl2-development.md`        | 0.5      | Low  |
| **Total**                         | **3–5**  | -    |

**Compare to "require WSL2"**

| Step                              | Days     | Risk |
| --------------------------------- | -------- | ---- |
| Re-write developer onboarding     | 1        | Low  |
| Re-test all auth flows on WSL2    | 1–2      | Med  |
| Workaround `/mnt/c/...` watcher   | 0.5–2    | High |
| Re-document IDE workflows         | 1        | Low  |
| User accepts switching IDEs       | -        | High (cultural) |
| **Total**                         | **3.5–6**| -    |

The numerical totals are similar; the risk profiles are not. The
Windows-native + platform-conditional path keeps us inside the
user's existing workflow; the WSL2 path forces a workflow change
that could surface its own bugs (auth, watchers, IDE friction)
which we'd then need to address on top of the original hang.

---

## 8. Cross-references

- [`cli-orchestration-survey-2026-05.md`](./cli-orchestration-survey-2026-05.md)
  § "Direct upstream evidence: claude-code #771" — the bug class
  that motivates R1.
- Same survey § R1, R2, R3, R4, R5 — the recommended-next-moves
  this document phases.
- ADR-0011 — names the WSL2 question and answers "no" with a brief
  rationale; this document is the long-form treatment.
- ADR-0013 — typed `CliRunEvent` adapter contract; platform-agnostic.
- ADR-0014 — stale-session reliability; platform-agnostic.
- [`path-forward-plan-2026-05.md`](./path-forward-plan-2026-05.md)
  — synthesis of survey + this decision into a single sequenced
  plan.
- Per-repo NOTES under
  `c:/Projects/agent-taskboard-devspace/cli-source-references/*/NOTES.md`
  — particularly:
  - `aannoo-hcom/NOTES.md` (Linux-only project; the "if we required
    Linux, we could be that" comparison).
  - `awslabs-cli-agent-orchestrator/NOTES.md` (tmux-substrate;
    requires WSL2 on Windows; pattern transfer is workarounds
    *not* substrate).
  - `github-copilot-sdk/NOTES.md` (Microsoft's own .NET
    `ProcessStartInfo` reference, Windows-native, no WSL2
    requirement, no hang reported).

---

## 9. Open follow-ups for whoever picks this up next

- Run survey § "Open questions" #1 (claude-code#771 inside
  `WebApplicationFactory`) before the next architectural
  commitment.
- Promote this document to ADR-0015 if accepted, with a 1-line
  abstract update to ADR-0011 pointing at it.
- If the answer changes (e.g. R1+R2 don't fix the hang, and the
  Windows-specific trigger turns out to require non-trivial
  P/Invoke), revisit § 5 vs § 7. The third option's cost grows
  super-linearly with the count of non-trivial Windows
  workarounds; at three or four such workarounds, "require WSL2"
  becomes more attractive than this document's recommendation.
- Re-check if `OperatingSystem.IsWindows()` branches are
  starting to leak from `Services/Cli/` into other namespaces.
  If they are, the abstraction is failing and the third option's
  cost is rising.
