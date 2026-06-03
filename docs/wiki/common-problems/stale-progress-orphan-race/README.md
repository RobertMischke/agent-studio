---
id: stale-progress-orphan-race
title: "Task folder disappears mid-pickup and lands in failed-pickup as an orphan"
status: open
first-seen: 2026-05-27T00:00:00Z
last-seen: 2026-05-27T23:59:00Z
severity: major
category: filesystem
tags: [filesystem, orphan, stale-progress, race-condition]
affects:
  - "3-progress lane"
  - "3a-failed-pickup recovery"
related-tasks: [human-decision-needed-feature-project-wiki-and-common-problems-library]
related-adrs: []
---

# stale-progress-orphan-race

**What.** A task folder disappears during pickup and is later represented as an orphan in failed pickup.
**Why.** Multiple lane writers, stale scanner state, or crash recovery can observe a folder between transitions.
**Workaround.** Use the failed-pickup recovery API path, not direct filesystem moves.
**Long-term.** Keep lane mutation single-writer and preserve enough recovery metadata to explain the move.
