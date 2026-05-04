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

## Verbindlich sehen, was passiert ist

The user must always have a confident, current picture of what the agents and the software did. Three rules follow:

- **No stale state.** A banner that claims "Agent is mid-task" must reflect the *current* run, not a previous one. When the truth changes, the surface updates within the same render frame; we never let an old signal linger.
- **Show errors plainly.** If a run errored, say so in the spot the user is looking at. Hidden failures, silent fallbacks, or "everything is fine" states draped over a real failure are worse than a red banner.
- **One signal per fact.** A failed run produces one explanation, not three. The orchestrator's deterministic decision messages, the system error, and the heuristic fallback should not all narrate the same event redundantly.

## Continuous over batch

The agent runs continuously; so does our view of it. We summarize as we go, not just at the end:

- Run summaries update during the run, not only after it exits. A long-running run still gives the user a current condensed view.
- Software-side aggregations (commits made, files touched, tests run) update at run end and refresh on the next read; they are not gated behind a separate user action.
- The session-level overview ("3 runs, 12 commits, last activity 5 min ago") follows from the run-level data and is never edited by hand.

## The orchestrator has visible memory

The orchestrator should feel present because it can explain what it knows, not because the UI pretends a hidden model is always awake.

The user must be able to answer:

- Which orchestrator am I talking to: global or project?
- Which session id, model, and CLI back this conversation?
- What was loaded when the orchestrator booted?
- Which job results, decisions, roadmap items, and open tasks are currently in memory?
- When was that memory refreshed?
- Which app action is the orchestrator proposing or taking?

Durable memory is a product surface. It should be visible, refreshable, and rebuildable from local evidence. The memory snapshot may be compact, but it must not be mysterious.

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
3. If the orchestrator speaks, can the user see what context it used?
4. Are we adding a new signal that duplicates an existing one?

If the answer to (1), (2), or (3) is no, redesign. If (4) is yes, suppress the new signal or replace the existing one - never stack them.
