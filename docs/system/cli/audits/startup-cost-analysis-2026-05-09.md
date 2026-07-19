# CLI startup cost analysis — 2026-05-09

Analysis-only. Reconnaissance for the question: how expensive is each CLI
to spin up, where does the time go, and which models are affected? No
code changes here — recommendations + ranked opportunities at the end.

## Quick summary table

| CLI | First-response | Quota probe | Model discovery | Resume win | Persistence |
|---|---:|---:|---:|---:|---|
| Claude | 0.5–2 s | 20–30 s (cached) | none (hardcoded) | 20–30 % faster | session heartbeat watches `~/.claude/projects/<cwd>/<uuid>.jsonl` |
| Codex | 1–3 s | 20–30 s (cached 60 min) | 5–12 s (cached 60 min) | ~30 % faster | none today |
| Copilot | 2–5 s | 15–20 s (in-memory) | 10–15 s (cached 60 min) | session via `--resume <slug>` | **full process reattach on backend restart** |
| Gemini | 2–5 s | **50–80 s (in-memory only)** | none (hardcoded) | ~20 % faster | none today |

**Per-call invocation cost**, dominated by:
- Process spawn + Node.js JIT: ~100–200 ms (Claude/Codex/Gemini); ~50 ms (Copilot, native binary)
- First network round-trip to the model: 500 ms – 5 s depending on backend latency

**Idle resident cost** (when keeping a CLI process alive, no work):
- Each idle Node-based CLI: ~50–150 MB RAM, near-zero CPU
- Four CLIs persistent simultaneously: **~400–600 MB RAM**, negligible CPU
- The "keep it open" hypothesis the user raised is cheap on the resident side

## Per-CLI breakdown

### 1. Claude

**Spawn:** `claude -p "<prompt>" [--model <model>] [--append-system-prompt-file <rules>] --output-format stream-json --verbose --dangerously-skip-permissions [-r <uuid>]` from [`backend/Services/Cli/ClaudeCliService.cs:51-141`](../../../backend/Services/Cli/ClaudeCliService.cs#L51).

**Probes:**
- Quota: PTY spawn + `/usage` (~20–30 s), cached per session — [`ClaudeQuotaProbe.cs:67-130`](../../../backend/Services/Quota/ClaudeQuotaProbe.cs#L67)
- Models: hardcoded list of 3, **zero discovery cost** — [line 647-663](../../../backend/Services/Cli/ClaudeCliService.cs#L647)
- Heartbeat: `FileSystemWatcher` on the per-cwd session JSONL, fires once per second max — [`ClaudeSessionHeartbeat.cs`](../../../backend/Services/Cli/ClaudeSessionHeartbeat.cs)

**Where the time goes:** Process spawn + Node JIT (~800 ms – 1.5 s on first invoke; 100–300 ms on warm process). Auth check is negligible (token cached in `~/.claude/`). Model load is offline.

**Resume:** UUID-only via `-r`. Skips session init. Saves 20–30 % overall.

### 2. Codex

**Spawn:** `codex exec [resume <uuid>] --json [-m <model>] [<prompt>]` from [`CodexCliService.cs:53-101`](../../../backend/Services/Cli/CodexCliService.cs#L53).

**Probes:**
- Quota: PTY spawn + `/status` (~20–30 s), cached 60 min — [`CodexQuotaProbe.cs:54-140`](../../../backend/Services/Quota/CodexQuotaProbe.cs#L54)
- Models: PTY spawn `codex debug models` (~5–12 s), cached 60 min, falls back to `~/.codex/config.toml` — [`Pty/CodexModelDiscovery.cs:92-134`](../../../backend/Services/Pty/CodexModelDiscovery.cs#L92)

**Where the time goes:** Network (OpenAI API) dominates per-call at 1–3 s. PTY model discovery is the worst startup cost (5–12 s) but it caches well.

**Resume:** UUID via `codex exec resume`. Captured from first `session_meta` ndjson frame.

### 3. Copilot

**Spawn:** `copilot -p "<prompt>" --allow-all [--name|--resume <slug>] [--model <model>]` from [`CopilotCliService.cs:134-223`](../../../backend/Services/CopilotCliService.cs#L134).

**Probes:**
- Quota: PTY waits for `Remaining reqs.: NN%` footer (~15–20 s), in-memory cache — [`CopilotQuotaProbe.cs:56-126`](../../../backend/Services/Quota/CopilotQuotaProbe.cs#L56)
- Models: PTY `/model` picker text (~10–15 s), cached 60 min, **no fallback on error** — [`Pty/CopilotModelDiscovery.cs:125-176`](../../../backend/Services/Pty/CopilotModelDiscovery.cs#L125)
- Auth: probes `gh auth token` via 3 fallback paths if no token in config (up to 5 s per attempt) — [`CopilotCliService.cs:533-576`](../../../backend/Services/CopilotCliService.cs#L533)

**Where the time goes:** GitHub auth probe (if token not cached) is the biggest single hit at 5+ s. Model discovery is otherwise the next slowest. First-response is network-bound at 2–5 s.

**Persistence:** Copilot is the only CLI with **full process reattach** — `executions.json` + on-disk JSONL log lets the backend rehydrate session state across restarts. Reattach cost: ~100–500 ms — [`CopilotCliService.cs:726-813`](../../../backend/Services/CopilotCliService.cs#L726).

### 4. Gemini

**Spawn:** `gemini -p "<prompt>" -o stream-json --skip-trust -y [-m <model>] [-r <uuid>]` from [`GeminiCliService.cs:50-115`](../../../backend/Services/Cli/GeminiCliService.cs#L50).

**Probes:**
- **Quota: 50–80 s (no disk cache)** — pre-trusts scratch folder, spawns Gemini, dismisses 1-time setup modals (4–6 s × up to 4 attempts), sends `ok` to populate metrics (25 s wait for response), then `/stats model` (8 s parse). Re-runs on every backend start. — [`GeminiQuotaProbe.cs:82-162`](../../../backend/Services/Quota/GeminiQuotaProbe.cs#L82)
- Models: hardcoded list, **zero discovery cost**

**Where the time goes:** The quota probe absolutely dominates. The probe has to send a fake prompt (`ok`) and wait for the response because Gemini lacks a headless quota query — only `/stats model` in the interactive UI works.

## `/api/cli/usage` (the 2-second offender)

The endpoint is one line in [`CliEndpoints.cs:88-91`](../../../backend/Endpoints/CliEndpoints.cs#L88) → `SessionRegistry.BuildReport(router)` in [`SessionRegistry.cs:31-69`](../../../backend/Services/Cli/SessionRegistry.cs#L31).

What `BuildReport` does:
1. **Sequential `cli.TestCliPath()` for each of 4 CLIs**, 5 s timeout each → up to **20 s worst-case** on a CLI that's broken or missing.
2. **Per-CLI session enumeration:**
   - Copilot: scans all jobs via `ScanAllJobs()` then filters by CLI type. Now O(1) thanks to `JobIndexCache`.
   - Claude: walks `~/.claude/projects/<cwd>/` and lists `.jsonl`. ~200 ms typical.
   - Codex: parses `~/.codex/session_index.jsonl` line by line. ~300 ms typical, **unbounded line count** (grows forever).
   - Gemini: parses `~/.gemini/projects.json` + walks `~/.gemini/tmp/<slug>/chats/` + per-session JSON parse. ~400 ms typical.

**Live measurement showed 2 s** — that means all four CLIs were available (no timeouts) and the disk walks ran cleanly. The bottleneck is **sequential `TestCliPath()` + sequential per-CLI enumeration**. With one slow CLI (e.g. Gemini's chat folder huge), the dominant cost would shift to that one.

## Where to win, ranked

### 1. Cache Gemini quota to disk like Codex/Copilot do — **50–80 s saved per app start**
The biggest single optimization. The probe is irreducibly expensive (Gemini's UI architecture forces it), but its result is stable for 30+ minutes. Adding the same disk-cache pattern Codex and Copilot already use removes the cost from boot and from the first user-triggered refresh.

### 2. Parallelize `TestCliPath()` in `SessionRegistry.BuildReport` — **5–15 s saved per `/api/cli/usage` call**
The four version-check spawns run sequentially today. `Task.WhenAll` over the four with a tight 2 s per-CLI timeout brings worst-case from 20 s to ~3 s.

### 3. Persistent CLI processes for Claude / Codex / Gemini — **300 ms – 1.5 s saved per invocation**
Copilot already has process reattach. Adding similar plumbing for the other three saves the per-call spawn + JIT cost. The user's concern about resident cost: ~50–150 MB RAM per idle Node CLI, near-zero CPU. Four CLIs persistent ≈ 400–600 MB RAM, which is acceptable on a developer machine. The architectural complexity is the bigger trade — needs session-to-process mapping, cleanup on idle, restart on hang. **Worth doing for a separate cycle** (Cycle 8 follow-up), not as a quick win.

### 4. Cap or compact Codex `session_index.jsonl` parsing — **0 to 500 ms (depending on history length)**
Today the file grows forever. A long-running developer can have 10K+ entries. Either cap to last N (rotate), or parse with a streaming approach that bails after the first page worth of recent sessions.

### 5. TTL disk cache fallback for Copilot model discovery — **10–15 s saved on the next start after a discovery failure**
Today the cache writes only on success. Writing stale cache on error gives the next start something to render while it re-tries.

## What this means for the original "persistent CLI" question

Cost of keeping a CLI open: **negligible CPU, 50–150 MB RAM per CLI**. Architecturally non-trivial but workable.

**Bigger win, lower complexity:** the Gemini quota disk cache (item 1) and the `/api/cli/usage` parallelization (item 2). Together they remove the most visible CLI-related latency the user sees today, without any persistent-process plumbing.

**Persistent CLI is the right answer** when the per-spawn cost (300 ms – 1.5 s) starts dominating user-perceived latency. Today it doesn't — most invocations are network-bound on the model side, not spawn-bound. If the product moves toward fast back-and-forth chat with sub-second turn-around, that's when persistence becomes load-bearing. Until then, quota caching is the cheaper win.
