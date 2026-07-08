# job-detail

Per-job side panel: prompt + protocol + git + log overlay + command deck + triage. The biggest single component in the codebase ([`job-detail.ts`](./job-detail.ts), 2451 LOC) — Cycle 10g will split it into sub-containers.

## Public API

Imports via `from './features/job-detail'`. See [`index.ts`](./index.ts).

**State services**:

- `JobSelectionService` (Cycle 9j) — `selected` signal, triage toast, `triageLanePeers` computed, URL sync (`?job=<id>&watchPath=<wp>`), open-detail request token guard.
- `TriageController` (Cycle 10c) — triage panel actions (move / move-to-top / delete / start), j/k peer navigation, auto-advance after mutation or external move, complete-and-next-review.

**Container components**:

- `JobDetailComponent` — the side panel itself; orchestrates 10 sub-panes.

**Cross-feature components**:

- `HygieneStripComponent`, `ProjectHygieneBadgeComponent` — used by review-lane card chips outside this feature.
- `ActivityLogViewComponent` — also embedded in verbose-debug.

**Utilities**:

- `parseActivityLog`, `buildConversationTurns`, `ActivityLogGroup`, `ActivityLogKind`.
- `classifyOutcome`, `OutcomeAssessment`, `QuickReply` — agent-outcome heuristics.

## Sub-folders

- `components/` — 9 sub-panes (cli-config-card, command-deck, detail-header, git-pane, hygiene-strip, log-overlay, pane-toggle-bar, prompt-pane, protocol-pane) + the activity-log machinery. The lane-action primary button + overflow menu live in `detail-header`; the action catalogue is headless in `state/triage-actions.model.ts`.
- `services/` — `git-pane.service`, `layout-panes.service` (job-detail-private, not exported via barrel).
- `state/` — the two cross-shell state services exported above.

## Notable patterns

- **Protocol verdict precedence (one head-state rule)**: the protocol pane's three-state verdict pill (`protocol-verdict.ts` → `deriveProtocolVerdict`) is derived from `status.md`, but the **current lane / review decision leads** it. `deriveProtocolVerdict` takes `laneState` (`TaskInfo.state`) and `orchestratorVerdict`; when the status.md-derived verdict is a run-outcome `Blocked`/`Failed` yet the card already lives in an accepted stand (`orchestratorVerdict === 'accept'`, or lane `6-completed`/`7-archive`), the head verdict leads with the accepted state and the blocker is demoted to `verdict.superseded` — a collapsed "Superseded run outcome" history strip, never the head banner. A stale Blocked from an overhauled context must not contradict an accepted stand. The pill + superseded strip render in the small `<app-protocol-verdict-banner>` child (`protocol-verdict-banner/`), which also owns the click-to-expand full-reason behaviour.
- **Visible verdict chain (Run → Gate → Review → Lane)**: after the head pill, `protocol-verdict-chain.ts` → `deriveVerdictChain` narrates the four decision points a task passes through, each with its own status dot and a link to its evidence (BEFUND 2: *"eine sichtbare Verdict-Kette … mit Links zur Evidenz"*). **Run** is the `status.md` outcome (carrying the *superseded* blocker when a stand overtook it), **Gate** is the deterministic runner-outcome/summary gate, **Review aspects** are the `reviewEvidence` findings (links jump to the finding), **Lane decision** is `orchestratorVerdict` reconciled with the lane — and it is flagged as the *leading* step (BEFUND 2 precedence). The chain also carries a one-line **causal narrative** (BEFUND 3) that links the earlier steps to the leading decision, e.g. *"Automated checks passed, but 1 high-severity review finding escalated this to human review"* — the concrete "warum eskaliert trotz 14 OK" link. It renders inside the same expand region as the reason/history in `<app-protocol-verdict-banner>`; the status.md evidence link opens the source viewer via the pane's `openSource`.
- **Robust blocked-reason parsing**: the blocked reason must be readable in full. The `[[TASK_BLOCKED:…]]` sentinel reason is captured lazily up to the closing `]]` (survives a stray `]`, quote, or colon), and the body-blocker sentence extractor only breaks a sentence at `.`/`!`/`?` followed by whitespace — so a file extension (`foo.ts`), a decimal (`5.1`), or an abbreviation no longer truncates the reason mid-word. The banner shows the whole reason via tooltip + click-to-expand.
- **Request-token guard**: every async detail load has a monotonic token; late replies for a stale job are dropped so the panel doesn't pop back open after Esc.
- **`triageLaneState` anchor**: walking peers and detecting external moves both key off this; the live `selected().info.state` can change under us.
- **`clearActingCallback`** bridge: TriageController doesn't depend on JobDetailComponent directly. The shell registers a closure that resolves the ViewChild lazily.
