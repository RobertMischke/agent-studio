---
concept: orchestrator
title: Orchestrator
learnMore: docs/architecture-decisions.md
learnMoreLabel: Architecture Decisions (ADR-0002)
---

The orchestrator is the deterministic loop that picks the next ready task per project, starts a CLI agent, watches its output, and decides what happens after the run.

It owns task pickup, lifecycle transitions, and the post-run policy. The CLI agent reports its own outcome with sentinels like `[[TASK_DONE]]` or `[[TASK_BLOCKED:...]]`; the orchestrator treats those as one input among several and is the single arbiter that moves a job between lanes.

A separate global session reasons across all watched projects and surfaces decisions as orchestrator messages alongside your own.
