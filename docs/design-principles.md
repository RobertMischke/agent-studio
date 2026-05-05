# Design Principles

The product principles that govern *how* this app feels and *what* it shows. Architectural decisions live in [architecture-decisions.md](architecture-decisions.md). Product scope lives in [README.md](../README.md) and [ROADMAP.md](../ROADMAP.md). This file holds the user-experience contract that ties them together.

## A layer on top of agents and software

This app is an abstraction layer on top of two systems:

1. **The agents** that do the work (Claude Code, Codex, Copilot, Gemini, the orchestrator).
2. **The software** the agents are changing (your repo, its commits, its files, its metrics).

Both layers run with or without us. The agents have their own logs and transcripts; the software has its own git history. What this app provides is a **single surface where you can see both, condensed at the top, and zoom in on demand**.

Two non-negotiables follow from that:

### 1. The software is always visible

You can always answer, *for any task or any moment in a task*:

- Which commits did the agent make on this step?
- What files did it touch?
- What changed in the codebase since the last review point?
- Which metrics moved (test counts, coverage, dependency changes, lint, build time)?

The change in your software is the unit of trust. A run that produced no commits is structurally different from one that produced ten, even if the agent's prose is identical. The UI surfaces that difference at the top level so you don't have to alt-tab into a terminal to find out.

### 2. Drill-down is always available

Every condensed view has a path to the underlying detail:

- A run summary expands to the run's full activity log.
- A run's commit list expands to the diff for each commit.
- An agent message expands to its tool calls.
- A tool burst ("12 reads, 3 edits") expands to the per-call list.
- A heuristic verdict ("Could not classify the agent's reply") expands to *why* the heuristic fired.

You should never see a high-level claim that you cannot interrogate. If we hide something at the top level for legibility, the path to the underlying evidence has to be one click away.

## Skills are action-driven report producers

Specialist Skills and script-backed loops are explicit actions. The user presses a button or the orchestrator asks for a named action: run a security audit, critique screenshots, run backend tests, run end-to-end tests, generate a source map, analyze module organization, or request the next design version. Broad creative, QA, and source-analysis work does not quietly happen everywhere by default.

Each action produces evidence. Human-readable Markdown is allowed and useful, but the app should also ask for a small structured report block with a schema version, status, summary, metrics, findings, and artifact paths. The structured block is an interface, not a wish. If the model or script fails to produce valid JSON, the UI keeps the raw Markdown visible, labels the report as unstructured, and lets the user inspect or turn it into a follow-up task manually.

The button is the contract boundary:

- The user can see what action is being triggered.
- The generated report lands beside the relevant task or project evidence.
- The app parses structured fields only when the contract is satisfied.
- The raw report stays available when parsing fails.
- Follow-up work becomes a normal queued task.

## Analysis reports are first-class product memory

Some actions are not task execution. They are inspections of the system itself: "are we on track?", "what drifted from the roadmap?", "which jobs are stale?", "did the last batch look healthy?", "which docs need sync?", or "what should be split into follow-up tasks?" These analyses may be manual, scheduled, or produced by a meta-cycle.

The output still follows the same evidence rule:

- Markdown is the human-readable artifact and must remain readable on disk.
- Structured JSON is the app contract when the analysis needs filtering, badges, trends, or follow-up automation.
- A failed JSON parse does not hide the report. The UI marks it as unstructured and keeps the Markdown.
- Reports carry scope: workspace, project, task, run, source branch, time window, and triggering action.
- Reports can reference Agent Message Bus records, runtime logs, screenshots, commits, task folders, and other reports, but they should not duplicate raw evidence wholesale.
- A finding that requires implementation becomes a normal queued task.

The UI should give analysis reports their own place at project level. They are not buried inside one task unless the analysis was explicitly task-scoped.

## Drift is a scored project dimension

Drift is the gap between what the project says and what the project does. It can happen between specs and tasks, tasks and jobs, ADRs and source code, README and product behavior, marketing and shipped reality, design references and screenshots, tests and risk areas, or runtime logs and expected behavior.

The project page should treat Drift as its own destination, not only as a filter inside Analysis Reports. A user should be able to trigger a Drift analysis, see a score, understand which dimensions contributed to it, and create normal follow-up tasks from the findings.

Drift scores are triage, not authority:

- Every score must link to evidence.
- Every dimension must show confidence and source coverage.
- A failed JSON parse must leave the Markdown report visible.
- A drift finding can suggest a task, patch, or documentation update, but it must not silently edit project state.
- The user must be able to see whether a drift item is new, accepted, ignored, already tracked, or resolved.

## Agent-facing steering context is visible

The instructions that shape agent behavior are part of the product experience. README files, AGENTS files, task contracts, runtime prompts, project settings, skills lookup sections, ADR indexes, and project-specific steering notes are not just repository plumbing. They explain why agents keep making certain choices.

The project page should therefore expose two layers:

- The raw technical documents, linked to their repository location and current revision.
- A shorter human summary that explains what agents are currently told, which rules matter most, which documents look stale or contradictory, and which recent failures suggest a documentation or process change.

When a meta-analysis finds a recurring failure pattern across jobs, the UI should connect the dots: evidence first, then the suspected steering gap, then the proposed README, AGENTS, skill, prompt, task-contract, or process update. The user can inspect the raw reports and create a normal follow-up task. The app should not silently change steering documents behind the user's back.

## See What Happened With Confidence

The user must always have a confident, current picture of what the agents and the software did. Three rules follow:

- **No stale state.** A banner that claims "Agent is mid-task" must reflect the *current* run, not a previous one. When the truth changes, the surface updates within the same render frame; we never let an old signal linger.
- **Show errors plainly.** If a run errored, say so in the spot the user is looking at. Hidden failures, silent fallbacks, or "everything is fine" states draped over a real failure are worse than a red banner.
- **One signal per fact.** A failed run produces one explanation, not three. The orchestrator's deterministic decision messages, the system error, and the heuristic fallback should not all narrate the same event redundantly.

## Continuous over batch

The agent runs continuously; so does our view of it. We summarize as we go, not just at the end:

- Run summaries update during the run, not only after it exits. A long-running run still gives the user a current condensed view.
- Software-side aggregations (commits made, files touched, tests run) update at run end and refresh on the next read; they are not gated behind a separate user action.
- The session-level overview ("3 runs, 12 commits, last activity 5 min ago") follows from the run-level data and is never edited by hand.

## A run is the unit of conversation

A *run* is one CLI invocation between two user inputs. A *session* is the ordered list of runs that make up a task's work. Inside a run there are turns, tool calls, and orchestrator decisions; across runs there is a story.

This shapes the file layout, the API, and the UI:

- Per-run artifacts (summary, log slice, commit set) live in `runs/run-NNN/` under the job folder. They are append-only, never rewritten across runs.
- A session-level index aggregates runs into the high-level view (`runs/index.json`).
- The UI's protocol pane is a vertical stack of run cards, each collapsed to its summary, expandable to its log + commits.

The runs file layout is documented in [filesystem-contract.md](filesystem-contract.md). When the runtime changes, the contract moves with it.

## Why these principles

The bet is that *humans are good at scanning condensed information and bad at scanning raw transcripts*. An agent run can be tens of thousands of tokens. A human reviewer needs the equivalent of a changelog entry, a git log, and a "click here for evidence" link. This document is the rule that keeps us from drifting back to a single big log file with no top-level surface.

When you propose a UI change or a backend service that touches the protocol, the activity log, or the run lifecycle, this file is the bar:

1. Does the top level still answer "what did the agent change in my software?"
2. Is the underlying detail one click away?
3. Are we adding a new signal that duplicates an existing one?

If the answer to (1) or (2) is no, redesign. If (3) is yes, suppress the new signal or replace the existing one. Never stack them.
