# Project Chat Progress Indicator and Responsiveness Research

Date: 2026-06-08  
Job: ASS-880  
Phase: 1 only, research and recommendation. No production UI code changed.

This document refreshes the earlier `project-chat-progress-indicator-2026-05-08.md` research (since retired) against the current code and adds local UI measurements from stable. The two Human Review open items are handled explicitly:

- Competitor screenshots and T0/T1/T2/T3 captures remain out of scope for this automated run because Copilot Chat, Claude Code VS Code, Cursor, Codex IDE surfaces, and ChatGPT Web are interactive, account-bound GUI sessions. A human-execute follow-up is recommended below instead of pretending public docs are empirical observation.
- Streaming backend work is still a separate follow-up. The current backend path is a blocking POST and cannot produce a true first-token or tool-use event without a transport/runtime change.

## 1.1 Current Implementation

The in-flight indicator is rendered by the shared chat component, not by a project-chat-specific component:

| Concern | Current reference |
|---|---|
| Indicator markup | `frontend/src/app/components/chat/chat/chat.component.html:132`, `<div class="chat__typing" data-testid="chat-typing">` |
| Pending send-button text | `frontend/src/app/components/chat/chat/chat.component.html:234`, button shows `...` while `pending()` |
| Pending input | `frontend/src/app/components/chat/chat/chat.component.ts:120`, `pending = input<boolean>(false)` |
| Submit event | `frontend/src/app/components/chat/chat/chat.component.ts:377`, `onSubmit` emits `submitMessage` |
| Indicator CSS | `frontend/src/app/components/chat/chat/chat.component.scss:534-551`, three animated dots |
| Host pending state | `frontend/src/app/features/orchestrator/components/orchestrator-side-sheet/orchestrator-side-sheet.component.ts:121`, `sending = signal(false)` |
| Optimistic user turn | `orchestrator-side-sheet.component.ts:703-725`, local turn is appended before the request finishes |
| Blocking send call | `orchestrator-side-sheet.component.ts:775-833`, one `sendOrchestratorChat` subscription clears `sending` on success/error |
| History polling | `orchestrator-side-sheet.component.ts:390`, 30 s visible interval when open and idle |
| HTTP client | `frontend/src/app/services/task.service.ts:1571` and `:1582`, history GET and send POST |
| Backend routes | `backend/Endpoints/RunnerEndpoints.cs:231` and `:240`, GET/POST `/api/runner/{project}/orchestrator-chat` |
| Backend send service | `backend/Services/Runner/OrchestratorChat.cs:428-601`, append user turn, wait on gate, resume model, append final reply |
| Project-chat read surface | `backend/Endpoints/ProjectChatEndpoints.cs:19`, `:47`, `:71`, `:88`, search/turn/stats/scroll |

Streams today:

- The indicator reacts only to the `pending` input, which the side sheet binds to `sending()`.
- There is no SignalR, SSE, token stream, CLI-output subscription, or polling event feeding this indicator.
- SignalR exists for task/job and bus events (`backend/Program.cs:373`, `:538`, `:550`, `:722`), but not for live orchestrator-chat progress.
- The virtual project-chat list reloads after send via `resetAndLoad()` (`orchestrator-side-sheet.component.ts:828`), which is a post-response sync point, not a live progress channel.

Visual states today:

| State | Visual behavior |
|---|---|
| idle | no indicator, composer enabled |
| composing | send button enabled when text or attachments exist |
| pending | user bubble is local and pending, three-dot typing indicator shows, composer disabled, send button shows `...` |
| done | indicator disappears after POST resolves and history refresh begins |
| error | local user bubble is marked non-pending with `errorMessage`; no separate chat-wide progress/error state |

Missing states: sent, queued, connecting, thinking, reading, tool-use, writing/streaming, done fade, and transport error.

Animation today:

- Three 6 px dots.
- 1.2 s infinite ease-in-out cycle.
- Staggered delays of 0.15 s and 0.3 s.
- Opacity moves from 0.3 to 1.
- Vertical transform moves each dot up by 2 px (`chat.component.scss:545-551`).

That is small, but the always-on vertical motion is visually louder than the embedded side-sheet context needs.

## 1.2 Competitor Observations

The prompt asks for empirical screenshots and T0/T1/T2/T3 for Copilot Chat, Claude Code, Cursor/Codex, and ChatGPT Web. I did not capture those in this automated run. The reason is capability, not effort:

- GitHub Copilot Chat in VS Code Insiders requires a live VS Code session and authenticated Copilot account.
- Claude Code VS Code extension requires an extension host and authenticated CLI/IDE surface.
- Cursor and IDE Codex surfaces are proprietary GUI apps with account-bound sessions.
- ChatGPT Web is authenticated and frequently protected by browser/session checks.
- Capturing frame timings requires a real panel recording or performance trace of those tools. Public docs cannot be used as "observed behavior" without violating the prompt's "no speculation" rule.

What can be used as design context, not empirical competitor evidence:

- The local mockup comparison already recorded VS Code/Copilot as the density and peripheral-status reference (`mockups/chat-window-next-gen/best-practices-comparison.md`, retired 2026-07 as design history).
- Copilot-style status should be quiet, peripheral, and readable.
- Claude Code-style terminal motion should not be copied into the embedded side sheet because constant spinner/shimmer motion pulls attention away from the transcript.

Follow-up task recommendation:

Create a human-execute research task that records one short prompt in Copilot Chat, Claude Code CLI, Claude Code VS Code, Cursor/Codex if available, and ChatGPT Web. Store screenshots under that task's `results/` folder and append a short empirical addendum to this document. Until then, Phase 2 can proceed from the measured local evidence and the literature-backed latency budgets below.

## 1.3 Literature Patterns

Relevant patterns and sources:

1. Nielsen Norman Group's response-time limits: about 0.1 s feels direct, about 1 s keeps flow, and longer waits need explicit feedback. Source: [Response Times: The 3 Important Limits](https://www.nngroup.com/articles/response-times-3-important-limits/).
2. Main-thread responsiveness matters separately from network wait. web.dev's INP guidance treats the next painted frame after an interaction as the responsiveness target, and recommends keeping interactions under 200 ms. Source: [Interaction to Next Paint](https://web.dev/articles/inp).
3. Long tasks are the right local diagnostic for "the UI feels frozen"; web.dev's long-task guidance centers on splitting work so the main thread can respond. Source: [Optimize long tasks](https://web.dev/articles/optimize-long-tasks).
4. MDN's web performance timing guidance says user feedback should be acknowledged within 100 ms, preferably 50 ms, and that feedback after about a second should clearly show the request is being handled. Source: [Recommended Web Performance Timings](https://developer.mozilla.org/en-US/docs/Web/Performance/How_long_is_too_long).
5. For chat specifically, optimistic UI is the correct pattern for the user's own turn and for the first progress frame: acknowledge the click locally before the server proves anything. This repo already does the user-turn half; the progress state should follow the same principle.

Implication for this surface: a subtle indicator should appear immediately, then evolve through honest status states. A louder spinner does not fix a slow first frame or a missing mid-flight event.

## 1.4 Local Timing and Bottlenecks

Measured against stable (`http://localhost:4011`, backend `5031`) with mocked orchestrator-chat POST responses, so no quota or live global session was involved.

Evidence files:

- `C:\Projects\agent-taskboard-workspace\projects\agent-taskboard\tasks\000\ASS-880\results\project-chat-pending-indicator.png`
- `C:\Projects\agent-taskboard-workspace\projects\agent-taskboard\tasks\000\ASS-880\results\project-chat-after-reply.png`
- `C:\Projects\agent-taskboard-workspace\projects\agent-taskboard\tasks\000\ASS-880\results\playwright\unknown\test-failed-1.png`
- `C:\Projects\agent-taskboard-workspace\projects\agent-taskboard\tasks\000\ASS-880\results\playwright\unknown\trace.zip`
- `C:\Projects\agent-taskboard-workspace\projects\agent-taskboard\tasks\000\ASS-880\results\playwright\unknown\video.webm`

Direct Playwright measurement:

| Measurement | Result |
|---|---:|
| T0 -> T1, click Send to `chat-typing` visible | 125 ms |
| T0 -> T3, mocked 4 s POST complete | 4103 ms |
| Long tasks during mocked wait | 434 ms across 5 long tasks |

Existing mocked E2E run:

- Command: `PW_TARGET=stable JOB_RESULTS_DIR=<ASS-880 results> npx playwright test e2e/orchestrator/project-chat-fix.spec.ts -g "sluggishness" --project=chromium`
- Because the grep matched the describe block, four tests ran.
- Result: 3 passed, 1 failed.
- Failure: sluggishness test logged `wall=4086ms longTaskMs=643 longTaskCount=7`, then failed its `<400ms` long-task ceiling at `frontend/e2e/orchestrator/project-chat-fix.spec.ts:225`.

Bottleneck analysis:

1. First frame is close, but not inside the target. The current optimistic path paints in 125 ms in the direct run. That is near the 100 ms responsiveness limit, but slightly over it.
2. There is still a main-thread budget problem. The previous research expected near-zero long tasks while waiting. Current stable measurements show 434-643 ms of long tasks across a 4 s mocked wait. The likely sources are side-sheet mount/render work, board/background updates, chat render recomputation, and periodic app surfaces sharing the same main thread. This should be profiled in Phase 2 with the existing `startLongTaskRecorder` helper around the exact send window.
3. T2 does not exist today. The backend path appends the user turn, waits on `SessionGate`, calls `ResumeWithFallbackAsync`, then appends and returns the final orchestrator turn (`OrchestratorChat.cs:443`, `:451`, `:488`, `:572`, `:599`). The frontend cannot show first token or tool-use status because no intermediate event is emitted.
4. The queue gate is a real hidden state. `SessionGate.WaitAsync` serializes all project-chat sends through the singleton global session. If a user sends while another chat is active, the UI still shows the same generic dots; it cannot distinguish queued from thinking.

## 1.5 Recommendation

Recommended Phase-2 design: a quiet Copilot-like status line, backed by a real UI state machine, with optimistic first paint and no new spinner library.

Visual style:

- Replace the bouncing three-dot indicator with a compact status row.
- Use a small static or opacity-only caret/dot, no vertical bounce, no shimmer, no mirrored/high-contrast effect.
- Keep it near the bottom of the chat body, just above the composer, so it reads as peripheral status rather than a full assistant message.
- Use low-contrast Catppuccin/studio tokens and respect `prefers-reduced-motion`.
- Keep the message text English and short.

States for v1, without backend streaming:

| UI state | Trigger | Suggested text |
|---|---|---|
| sending | local submit accepted, before/while upload begins | Sending |
| queued | optional, if a future backend event exposes gate wait | Queued |
| thinking | POST in flight after send | Thinking |
| working | elapsed >= 8 s with no reply | Working |
| still-working | elapsed >= 30 s | Still working |
| received | POST success, before fade-out | Done |
| error | POST failed or error turn created | Send failed |

States for v2, when streaming exists:

| UI state | Event source |
|---|---|
| reading | backend says context/files are being prepared |
| running-tool | backend emits `tool.call.started` with a tool name |
| writing | first token or first content chunk |

Latency budgets:

| Budget | Target |
|---|---:|
| T0 -> first visible status frame | <= 100 ms, stretch <= 80 ms |
| Long task total during first 5 s after send | <= 250 ms initially, then tighten toward <= 50 ms per 5 s window |
| No individual long task during active response | >50 ms should fail the strict Phase-2 regression once hotspots are removed |
| T2 for v1 wall-clock state change | `Thinking` visible by 600 ms even without backend events |
| T2 for v2 stream event | first backend progress event visible <= 1500 ms |
| Indicator removed after final response | API roundtrip plus <= one rendered frame |

Code touchpoints for Phase 2:

- `frontend/src/app/components/chat/chat/chat.component.*`: replace `chat__typing` markup/CSS, add status text and timer-driven state.
- `frontend/src/app/features/orchestrator/components/orchestrator-side-sheet/orchestrator-side-sheet.component.ts`: optionally pass a richer progress input once the shared chat component supports it; preserve existing optimistic user turn behavior.
- `frontend/e2e/helpers/timing.ts`: use `clickToVisible`, `apiRoundtrip`, and `startLongTaskRecorder`.
- `frontend/e2e/orchestrator/`: add a dedicated perf/visual spec for sending, thinking, long-wait, error, and completed states with mocked backend.

Do not change in Phase 2 unless explicitly signed off:

- No new spinner dependency.
- No server push frequency increase.
- No backend streaming implementation inside the visual redesign task.
- No competitor-timing claims without human-captured evidence.

## Follow-Up Tasks

1. Human-execute competitor addendum: capture Copilot, Claude Code, Cursor/Codex, and ChatGPT Web screenshots plus T0/T1/T2/T3 timings, then append them here.
2. Project chat v2 streaming backend: add a SignalR or streaming transport for progress events from the orchestrator chat backend, including queued/thinking/tool/writing events and a first-progress-event budget.
3. Main-thread profiling cleanup: before or during Phase 2, profile the 434-643 ms long-task budget observed in this research and identify the top render/update hotspots during the pending window.

## Phase-2 Sign-Off Boundary

Phase 2 should start only after the recommendation above is accepted. The minimum accepted scope should be:

- Quiet status-row visual style.
- Optimistic first status frame.
- Timer-driven v1 states.
- Playwright timing assertions using mocked chat endpoints.
- Visual evidence for idle, sending/thinking, long-wait, done, and error.

This document does not implement those changes.
