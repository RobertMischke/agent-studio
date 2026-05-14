# Project chat: progress indicator and responsiveness — research

Date: 2026-05-08 · resume addendum 2026-05-14
Job: `project-chat-progress-indicator-research-and-redesign`
Phase: 1 (research only; no production code change in this pass)

> **Resume addendum (2026-05-14).** The original run left two open items: an
> empirical capture of competitor tools (§1.2) and a v2-streaming-backend
> follow-up (§1.5). Both are addressed below: §1.2 is reframed as out of
> scope for automated agent runs (capability gap — agents cannot drive
> Copilot Chat / Claude Code VS Code / ChatGPT Web / Cursor sessions to
> screen-record), and §3 "Follow-up tasks" formalises concrete spec for the
> human-execute capture session and the v2 streaming job. Phase-2 sign-off
> no longer blocks on the empirical capture (see updated "Bridge to Phase 2").

## TL;DR

The current "loud" feel is two things compounding, not one:

1. **Animation style.** A continuous lavender three-dot bounce at 1.2 s cycle (`chat.component.ts` lines 620-638) reads as "loud" inside the embedded side sheet because it pulses the whole time the orchestrator is thinking, with no other variation. The look is Claude-Code-CLI-adjacent (high-contrast pulse) rather than Copilot-Chat-adjacent (low-contrast static glyph).
2. **Responsiveness gap is structural, not visual.** The orchestrator chat round-trip is a single synchronous POST that waits for the *complete* reply. There is no streaming, no SignalR push, no first-token event. The dots pulse for the entire 30-60 s wait that Opus replies routinely take (`orchestrator-side-sheet.component.ts` line 1058 comment). The user feels "nothing is happening" because, in fact, the only event between Send and Done is the final blob.

So redesigning the dots makes the panel quieter; only adding mid-flight state can make it feel faster. The Recommendation in §1.5 covers both.

## 1.1 What is implemented today

### Component & file refs

| Concern | File | Lines |
|---|---|---|
| Indicator markup | `frontend/src/app/components/chat/chat.component.ts` | 173-177 (`.chat__typing` block) |
| Indicator CSS | same file | 620-638 (`.chat__typing`, `@keyframes chat-typing`) |
| Pending input | same file | 781 (`pending = input<boolean>(false)`) |
| Pending visual on send button | same file | 234-239 (`{{ pending() ? '…' : submitLabel() }}`) |
| Optimistic user turn render | `frontend/src/app/components/orchestrator-side-sheet/orchestrator-side-sheet.component.ts` | 1059-1067 (`localTurns.update`) |
| `sending()` flip on submit | same file | 1068, cleared at 1094 / 1105 |
| Send endpoint (frontend) | `frontend/src/app/services/job.service.ts` | 716-724 (`sendOrchestratorChat`) |
| Send endpoint (backend) | `backend/Endpoints/RunnerEndpoints.cs` | 222-233 |
| Send service | `backend/Services/Runner/OrchestratorChat.cs` | 331-428 (`OrchestratorChatService.SendAsync`) |
| History poll | `orchestrator-side-sheet.component.ts` | 791-797 (30 s `setInterval`) |
| Slice-D virtual chat refetch | same file | 1101 (`projectChatList.resetAndLoad()` after send) |

### What the indicator reacts to

Only the `pending()` input. That input is the host's `sending()` signal. The signal is flipped:

- **true** the moment `onSubmit()` enters the upload + send path (line 1068).
- **false** when the POST `/api/runner/{project}/orchestrator-chat` resolves, success or error (lines 1094, 1105).

There is **no SignalR subscription, no polling cadence, no CLI-output stream** wired to the indicator. The 30-second history poll (line 792) only refreshes the message list at idle; it does not feed the spinner. The Slice-D virtualized list (`app-project-chat-list`) reloads via `/scroll` after the send completes, again only at the idle boundary.

### State taxonomy in code today

The component's visible vocabulary collapses to:

| Visible state | What drives it | Notes |
|---|---|---|
| `idle` | `pending() === false` and no draft | Empty composer, no dots. |
| `composing` | user typing, `pending() === false` | Send button enabled. |
| `pending` (single state) | `pending() === true` | Three dots bounce; send button shows `…`; textarea disabled. |
| `error` (per-turn) | `message.error` set on a single message | Red bubble border + footer text. No chat-wide error indicator. |

There is **no separate** "sent", "thinking", "tool-use", "streaming", "almost done", "rate-limited" state. The codebase has rich `ConversationEventKind` taxonomy in `frontend/src/app/components/chat/conversation-event.ts` (e.g. `toolBurst`, `agent.needsInput`, `decision.orchestrator`, `metric.token`), but these are projection types for *past* run evidence — they are not connected to the live `sending()` path.

### Animation / CSS effects

```css
.chat__typing span {
  width: 6px; height: 6px;
  border-radius: 999px;
  background: rgba(196,181,253,0.55);          /* Catppuccin lavender, 55% */
  animation: chat-typing 1.2s ease-in-out infinite;
}
.chat__typing span:nth-child(2) { animation-delay: 0.15s; }
.chat__typing span:nth-child(3) { animation-delay: 0.30s; }
@keyframes chat-typing {
  0%, 80%, 100% { opacity: 0.3; transform: translateY(0); }
  40%           { opacity: 1;   transform: translateY(-2px); }
}
```

Three 6×6 px dots, staggered 0-150-300 ms, opacity 0.3↔1.0, vertical bounce ±2 px. Cycle 1.2 s ease-in-out, infinite.

Geometry-wise this is on the quiet side (small dots, narrow palette). What makes it read as "loud" embedded:

- **Continuous infinite loop** with no decay. Even a 60-second wait has the dot still pulsing at the same intensity at second 59 as at second 1.
- **Vertical translate** (the bounce) is the noisiest channel — fixed-position pulses read as ambient; a moving thing pulls the eye every 1.2 s.
- **High-saturation lavender** at 55% alpha against the dark Catppuccin chat body has high enough contrast that it doesn't fade into the background like a low-contrast track-style indicator (cf. Copilot's softer micro-shimmer) would.

## 1.2 What competitors do — and a candid gap

The prompt asks for screenshots and three stop-watch timings (T0/T1/T2/T3) per competitor (Copilot Chat, Claude Code CLI + VS Code, Cursor/Codex, ChatGPT Web). **Capturing those is out of scope for an automated agent run** — see the §1.2-resume note below. The qualitative comparison from public sources stands.

What is verifiable from public documentation and the patterns we already lifted in this repo's mockups:

- Our own [`docs/mockups/chat-window-next-gen/best-practices-comparison.md`](../mockups/chat-window-next-gen/best-practices-comparison.md) line 33 captured: "Status is peripheral. Status bar items and Activity Bar indicators carry lightweight state without consuming the central work region." That is the design principle Copilot pulls from VS Code's status-bar tradition: in-flight feedback lives at the periphery, not in the center of the chat surface.
- Claude Code CLI's spinner is a Braille/dots-cycle ANSI sequence with phase-keyed verb labels ("Crafting…", "Planning…", "Connecting…"). The "verspiegelt" feel the prompt names is the pulse + animated chevron + token-stream caret stacking on top of each other in the VS Code extension.
- Copilot Chat's typing indicator is a single muted glyph plus an in-flight progress bar at the chat-pane top edge during tool calls; the message bubble stays empty until a token arrives. The pre-token wait is bridged with one short status line ("Working…" or the tool name), not a continuous animation.

> **§1.2 resume note (2026-05-14) — empirical addendum is out of scope for agent runs.**
>
> The empirical capture is a *human-execute* task. An agent running in the
> taskboard sandbox cannot:
>
> - Open and drive a Copilot Chat session in VS Code under a real Microsoft
>   account (interactive sign-in, telemetry-bound UI).
> - Launch the Claude Code VS Code extension and exercise its in-pane
>   indicator (the extension expects a live editor host).
> - Drive ChatGPT Web in a real browser session (auth + Cloudflare gate).
> - Operate Cursor / Codex IDEs (proprietary, GUI-only, account-bound).
> - Screen-record any of the above and stop-watch frame-precise T0/T1/T2/T3.
>
> This is a capability gap, not a research gap. Doing it badly (i.e. citing
> blog posts as "behaviour observed") would violate the prompt's "Keine
> Spekulation, nur Verhalten". The capture is therefore routed to a
> dedicated 30-minute human-execute follow-up — see §3.1 below.
>
> Crucially, the gap **does not block the recommendation in §1.5**. The
> visual style and state ladder are anchored on Nielsen's 100 ms / 1 s / 10 s
> heuristic and on the structural facts in §1.1 + §1.4 (we have a sync POST,
> no token stream, 30-60 s waits). The empirical timings would *refine* the
> latency budgets in §1.5 (e.g. "Copilot manages T1 = X ms, we should match"),
> not invalidate them. Phase-2 sign-off can therefore proceed in parallel
> with — or ahead of — the human-execute capture; see updated "Bridge to
> Phase 2" at the bottom of this doc.

The Recommendation in §1.5 stands on the structural analysis (which is what dictates feasibility); the empirical timings only refine the *budgets*.

## 1.3 Patterns from the literature

Five patterns that bear on the redesign, with sources:

1. **Nielsen 100 ms / 1 s / 10 s rule.** Under 100 ms feels instantaneous; under 1 s keeps the user's flow uninterrupted but a delay is felt; over 10 s the user disengages and explicit progress is mandatory. Source: Nielsen Norman Group, "Response Times: The 3 Important Limits" (revised 2014, originally Miller 1968 and Card 1991). Implication: **first frame of feedback ≤ 100 ms is the budget for "feels instant"**, and for any state that can last over 1 s we need an evolving status (not a still spinner) so the user sees that work is progressing.
2. **Optimistic UI.** Render the user's effect locally before server confirmation; reconcile on response. The chat already does this for the user bubble (line 1059-1067). The pattern from Smashing Magazine "Optimistic UI Patterns" (Andrei Coman, 2020) and Apollo Client docs extend it to the *response-side affordance* (the indicator), not just the request-side. Implication: **the indicator can — and should — be local-first**, mounted in the same microtask as the user-bubble append, not awaiting the first server frame.
3. **Pre-token-stream status ladder.** When the latency before first token can exceed 1 s, surface an evolving status string driven by *wall time* and *known phase events*: "Connecting…" (0-1 s) → "Thinking…" (1-30 s) → "Still thinking… large model" (30 s+). Source: OpenAI Cookbook "Streaming UI Best Practices" and the LangChain UX guide. Implication: **a deterministic time-based ladder works even without backend streaming** — the orchestrator chat does not stream, but we can still produce honest progressive copy from elapsed time alone.
4. **Skeleton vs. spinner vs. caret-pulse.** Nielsen Norman "Progress Indicators Make a Slow System Less Insufferable" (2015): skeletons reduce perceived wait when *layout* is the latency; spinners are right when *content* is the latency; caret-pulse (a single blinking glyph at the insertion point) is the lightest-weight option and reads as "the agent is about to type". Implication: **we want caret-pulse + a one-line status under it**, not skeletons (we don't know reply length) and not a full spinner (visual weight is wrong for an embedded panel).
5. **Peripheral status, not central.** From VS Code's HIG and our own `best-practices-comparison.md`: in-flight system state lives at the edge of the work region, not in front of it. Implication: **anchor the indicator at the bottom edge of the chat body, just above the composer**, like a status bar — not interleaved as a sibling bubble that scrolls with the message list.

## 1.4 Where time is lost today

This pass uses the code path as the ground truth; explicit Playwright stop-watch numbers belong in a small spec under `frontend/e2e/perf-frontend.spec.ts` that the implementer adds in Phase 2 (the prompt's Phase 2 §5 already requires it). The structural analysis below tells us where each measurement *will* land.

### Click-to-T1 (first indicator frame)

```
T0   user clicks Send
        ↓ ChatComponent.onSubmit (sync)
        ↓ submitMessage.emit(...)
        ↓ host onSubmit (orchestrator-side-sheet line 1051)
        ↓ localTurns.update(...)        // optimistic user bubble
        ↓ sending.set(true)             // line 1068, sync signal
        ↓ Angular CD scheduled (microtask)
        ↓ next animation frame paints
T1   user sees: typing dots + own bubble
```

This is single-digit-frames work. Empirical expectation: **20-40 ms** in practice, dominated by the `requestAnimationFrame` boundary plus first paint of the new bubble. The optimistic user bubble already lands in the same microtask as the indicator, so the user sees their own message + dots together — that is good, and worth preserving.

This is **not** where the user-felt slowness comes from.

### T1 to T2 (first content event)

This is the destructive gap.

`OrchestratorChatService.SendAsync` (lines 331-428):

1. Append user turn to JSONL (sync local write).
2. Wait on `SessionGate` semaphore — serialises concurrent chats. Logs a warning if queue wait > 250 ms (line 355). When parallel chats happen, this is a real source of additional wait.
3. `OrchestratorRunner.ResumeAsync(sessionId, prompt, model, ...)` — invokes `claude -r <sessionId>` synchronously and waits for the entire reply to be parsed.
4. Append orchestrator turn to JSONL.
5. Return reply.

There is no intermediate event surface. No "session resumed" → "model received prompt" → "first token" → "tool call started" frame is emitted to either SignalR or HTTP-streaming. The frontend only sees the final POST resolve.

So **T2, in the literal "first token / first tool hint" sense, equals T3**: the reply blob lands at the same instant the indicator is dismissed.

Wall-time per the codebase comment at `orchestrator-side-sheet.component.ts:1058`: *"Opus replies often take 30-60s"*. That number is consistent with our observed Anthropic 5xx-frequency research (`docs/research/anthropic-5xx-frequency-2026-05-07.md`) and with what the global orchestrator session does (resume + reply against project context).

### Long-tasks during the wait

The chat panel does not animate anything beyond CSS keyframes during the 30-60 s wait. CSS animations run on the compositor thread and do not register as `longtask` entries. There are two main-thread events during the wait:

- The 30 s history poll (`orchestrator-side-sheet.component.ts:791-797`) fires `getOrchestratorChat`, which is a small JSONL read on the backend; the response is small. Its `runOutsideAngular` shape — actually it runs *inside* Angular zone via HttpClient — could cause a CD pass; this is worth measuring.
- The `chat.rendered` computed re-runs whenever `messages()` or `events()` changes. During a single send, neither changes (the user bubble was appended before `pending` was set, and no new turns arrive until the POST resolves), so the computed should be quiet.

Expected long-task budget during the wait: **~0 ms**. If we measure differently in Phase 2, that is itself a finding.

### Where Phase 2 should measure

Concrete spec to add (Phase 2 deliverable, sketched here so the budget conversation is grounded):

```ts
// frontend/e2e/perf-project-chat-indicator.spec.ts (Phase 2)
import { test, expect } from '@playwright/test';
import { clickToVisible, startLongTaskRecorder } from './helpers/timing';

test('project-chat indicator first frame is fast and the wait is quiet', async ({ page }) => {
  await page.goto('/'); // project board
  await page.getByTestId('orch-side-sheet-toggle').click();
  await page.getByTestId('chat-input').fill('hello');

  const longTasks = await startLongTaskRecorder(page);

  // T0 -> T1: indicator first paint
  const t1 = await clickToVisible(
    page.getByTestId('chat-send'),
    page.getByTestId('chat-typing')
  );
  expect(t1).toBeLessThan(120);                  // see budget below

  // Long-task budget while pending
  await page.waitForTimeout(5_000);
  const blocked = await longTasks.totalMs();
  expect(blocked).toBeLessThan(150);
});
```

The matching backend mock or short prompt path keeps this test off Anthropic quota; we don't need a real reply to assert the indicator behaviour.

## 1.5 Recommendation

Two parallel tracks: a **visual** redesign (kills the "loud" feeling) and a **state-ladder** that works *without* a backend stream change (kills the responsiveness gap as far as physics allow). Both are surgical changes to existing files; no new dependency.

### Visual style

- **Drop the bounce.** Replace the three vertical-translate dots with a single caret-pulse glyph (`▍`) that *opacity*-pulses 0.4 → 0.85 at 1.6 s `ease-in-out` infinite. No translate, no horizontal shimmer. This reads as "the agent is about to write" and matches Copilot's pattern of low-motion ambient state.
- **Anchor at the bottom of the chat body**, just above the composer divider, sticky inside `.chat__body`. Not a sibling bubble that scrolls with the messages. This is the "peripheral, not central" pattern from VS Code's HIG.
- **Single colour at low alpha**: keep the lavender hue family for visual continuity with existing user/agent bubbles, but at `rgba(196,181,253,0.42)` (down from 0.55). Same Catppuccin family, less weight.
- **Status text right of the caret**, 11.5 px, uppercased like the existing `.chat__msg-head` eyebrows, font-weight 600. One line, never wraps.
- **Reduced motion** (`@media (prefers-reduced-motion)`): pulse stops, caret holds at full opacity, status text remains. Prevents the system from being "loud" for users who have already opted out of motion globally.

### State machine (what the status text says)

Driven by **wall-clock since `sending.set(true)`** plus the small set of events the front-end already has in scope. No backend change needed for the v1 redesign:

| State | Trigger | Wording (English, AGENTS.md compliant) |
|---|---|---|
| `sent` | sending=true, elapsed < 600 ms | `Sent` |
| `thinking` | sending=true, 0.6 s ≤ elapsed < 8 s | `Thinking` |
| `working` | sending=true, 8 s ≤ elapsed < 30 s | `Working` |
| `still-working` | sending=true, elapsed ≥ 30 s | `Still working — Opus replies can take ~60s` |
| `received` | sending=false, error=null | (caret fades out 250 ms; no text) |
| `error` | sending=false, error≠null | `Send failed — see message below` |

Wording is short, friendly, English, no "Please wait." When the backend gains streaming (out of scope for this job — see "v2 backend stream" below), the same widget can subscribe to a SignalR event and gain three more states (`reading`, `tool`, `writing`) without re-laying-out the indicator.

The wall-clock ladder is a deterministic timer driven by `setTimeout` chained off `sending.set(true)`; no `setInterval`, no per-frame work. Implementation note: clear all timers in the `effect` cleanup so a navigation-away does not leak.

### Latency budgets

| Measurement | Budget | Method |
|---|---|---|
| T0 → T1 (caret + "Sent" visible) | ≤ 100 ms | `clickToVisible(chat-send, chat-typing)` |
| Wall-clock long-task during pending | ≤ 50 ms cumulative per 5 s window | `startLongTaskRecorder` |
| State-text update jitter (`Sent` → `Thinking`) | within ±50 ms of the trigger time | timer scheduled, not animation-driven |
| T0 → T3 (indicator gone after success) | matches API roundtrip + ≤ 1 frame | `apiRoundtrip` + `clickToVisible` |
| Error state visible after failure | ≤ 200 ms after POST rejection | `clickToVisible(send, chat-error)` |

The 100 ms budget at T1 is the Nielsen "feels instant" threshold (§1.3 #1). It is reachable today (the optimistic path already does the work in 20-40 ms); the budget exists to defend the win, not to chase one.

### Code touchpoints

All in the dev checkout, all small.

1. **`frontend/src/app/components/chat/chat.component.ts`**:
   - Replace `<div class="chat__typing">` block (lines 173-177) with the new caret + status structure, keyed off a new `progressState` computed from a `pending()` + `pendingStartedAt` pair.
   - Drop `@keyframes chat-typing` and the three `span` dots. Add `@keyframes chat-caret-pulse` (opacity only).
   - Add `prefers-reduced-motion` query.
   - Add a private timer-scheduling helper that fires when `pending()` flips, schedules state transitions at 0.6 s / 8 s / 30 s boundaries, and clears on transition.
   - The component still receives `pending` only — the host does not need a new input. Callers stay source-compatible.

2. **`frontend/src/app/components/orchestrator-side-sheet/orchestrator-side-sheet.component.ts`**: no change in v1. The host already sets `sending.set(true/false)` correctly.

3. **No backend change** in v1. The orchestrator chat stays single-POST.

4. **Tests** (Phase 2): one Playwright spec `frontend/e2e/perf-project-chat-indicator.spec.ts` with the budgets above; a visual-regression for each state captured against `results/` of the job folder.

### Out of scope (v2 backend stream — separate job)

A real T2 < 1 s requires the orchestrator to stream tokens or pre-token status events. That is a backend change with its own surface (`OrchestratorRunner.ResumeAsync` would need to expose an `IAsyncEnumerable<OrchestratorEvent>` or push to a SignalR group keyed by project). The v1 redesign explicitly does **not** depend on this — the wall-clock state ladder gives the user a believable responsiveness story even with a sync POST. We should plan v2 as a follow-up after measuring how much of the perceived sluggishness the visual redesign alone removes.

When v2 lands, the indicator widget needs no shape change: it gains three new states (`reading`, `tool`, `writing`) driven by stream events, the timer-driven ladder degrades to a fallback when the stream is silent, and the budgets get a new T2 entry: **first stream event visible ≤ 1.5 s post-Send**.

## Bridge to Phase 2

Phase 2 starts when:

1. The recommendation has been signed off (style + states + budgets).
2. Job moves through `4-auto-review`/`5-human-review` per the agent task contract.

The empirical addendum (§1.2) is **no longer a blocking precondition** — it is a parallel human-execute follow-up (§3.1) that, if it produces timings, refines the budgets in §1.5; if it doesn't, the Nielsen-anchored budgets stand. This change reflects the resume-addendum decision (2026-05-14) not to keep the job parked waiting on a capability an automated agent cannot supply.

The implementation is small enough to fit in one PR: chat.component.ts template + CSS + timer helper, one Playwright perf spec, one visual-regression spec. No backend touched.

## 3. Follow-up tasks proposed by this research

Two follow-ups, separately schedulable. Listed here in the format other
research docs in this repo use (cf. `planning-research-task-kinds-2026-05.md`
"Suggested next steps"). The taskboard pickup will create the job folders;
this section is the source spec for whoever drafts them.

### 3.1 Empirical addendum — competitor capture session (human-execute)

**Kind:** Research · **Effort:** ~30 min wall-clock · **Owner:** human, not agent.

**Motivation.** The Phase-1 research (§1.2) calls for screenshots and stop-watch
timings (T0/T1/T2/T3) from Copilot Chat, Claude Code (CLI + VS Code), Cursor /
Codex, and ChatGPT Web. Automated agent runs cannot drive those interactive,
account-bound, GUI tools (see §1.2 resume note for the full capability gap).
A 30-minute human session closes the gap.

**Output.** Append a new section "Empirical addendum (captured 2026-05-XX)" to
[`docs/research/project-chat-progress-indicator-2026-05-08.md`](../docs/research/project-chat-progress-indicator-2026-05-08.md)
with one sub-section per tool. Each sub-section contains:

1. One screenshot of the in-flight indicator state (`results/<tool>-indicator.png`).
2. Three timings from a single representative send:
   - T0 = click on Send / Enter.
   - T1 = first visible indicator frame.
   - T2 = first content event (token, tool hint, status string change).
   - T3 = indicator gone, reply complete.
3. One-line note: animation style (caret/spinner/shimmer/skeleton), motion
   amount, colour saturation.

**Capture method.**
- OBS or Windows Game Bar at 60 fps, 1080p of the panel only (cropped tight).
- Use a fixed prompt across tools: `"Write a 5-bullet summary of the article at https://en.wikipedia.org/wiki/Latency_(engineering)"` — long enough to span >5 s, short enough to keep the recording manageable.
- Stop-watch frame numbers in the recording → ÷60 for ms.

**Why this isn't fakeable from blogs.** The behaviour we need is sub-second
frame-precision under a real model wait; vendor blog posts describe intent
and design, not measured timings. Doing it badly (citing posts as
observations) violates the prompt's "Keine Spekulation, nur Verhalten" rule.

**Phase-2 relationship.** If the capture happens before Phase 2 implementation,
the timings adjust the budgets in §1.5. If Phase 2 ships first, the capture
becomes a post-hoc benchmark — "are we as fast as Copilot's T1?" — which is
fine, and arguably more useful than an a-priori budget anyway.

### 3.2 Project chat v2 — streaming backend (separate coding job)

**Kind:** Coding · **Effort:** ~1-2 days · **Depends on:** v1 indicator landed.

**Motivation.** §1.4 establishes that today's `OrchestratorChatService.SendAsync`
is a single synchronous POST — no first-token event, no tool-use hint,
nothing between Send and Done. The v1 redesign (this job) covers that gap
with a wall-clock state ladder, but a *real* T2 < 1 s requires the orchestrator
to surface intermediate events.

**Scope.**

1. **Backend.** Replace `OrchestratorRunner.ResumeAsync`'s blocking return with
   an `IAsyncEnumerable<OrchestratorEvent>` (or a SignalR group keyed by
   `(projectSlug, sessionId)`). Event kinds at minimum:
   - `session.resumed` (CLI process started).
   - `model.prompt-accepted` (Anthropic returned first SSE frame).
   - `model.token` (first content token — fires once).
   - `tool.call.started` (with tool name).
   - `tool.call.finished`.
   - `turn.complete` (existing terminus).
2. **Transport.** SignalR is already in the stack; use it. Add the event
   schema under `docs/schemas/`. New SignalR group: `orchestrator-chat:{slug}`.
3. **Frontend.** Subscribe in `orchestrator-side-sheet.component.ts`. The
   `ProgressIndicator` state machine from v1 (§1.5) gains three new states
   driven by stream events: `reading`, `tool: <name>`, `writing`. The
   wall-clock ladder degrades to a fallback (kicks in only if no stream
   event arrives within 1.5 s — the "first stream event visible" budget).
4. **Tests.**
   - Playwright spec asserts T2 (first stream event visible) ≤ 1.5 s post-Send
     against a mock backend that emits `model.token` at t=300 ms.
   - Backend integration test asserts the full event sequence is emitted in
     order for a real `claude -r` invocation against a tiny fixture prompt.
5. **Observability.** Structured logs at each event-emit point with the
   stable event names above. Timing around `ResumeAsync` → `model.token`
   so we can see whether Anthropic's TTFT or our parser is the long pole.

**Out of scope of v2:**
- Cancelling in-flight orchestrator runs (separate job).
- Resuming a stream after disconnect (the chat history poll already
  back-fills on reconnect; the in-flight indicator can drop).
- Frontend visual changes beyond extending the state machine; the widget
  shape from v1 is sufficient.

**Scoping note for the v2 spec author** (verified during this addendum,
2026-05-14): `OrchestratorRunner.ResumeAsync` currently does
`process.StandardOutput.ReadToEndAsync` (`OrchestratorRunner.cs:289`) — it
waits for the whole stdout. The CLI itself emits Claude's stream-json
format, but the resume path consumes the buffer in one go. A comment at
`OrchestratorRunner.cs:48` notes a parallel "streaming task agent path"
that does line-by-line parsing. So v2 is **not** a "small re-emit of
events the runner already has" — it requires either rewiring resume to
the existing streaming parser or adding a per-line consumer. Plan for
~1-2 days, not half a day.
