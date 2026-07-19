---
id: completion-gate-dead-code
title: "Completion gate existed but was not wired into the review path"
status: fixed
first-seen: 2026-06-05T00:00:00Z
last-seen: 2026-06-05T00:00:00Z
severity: blocker
category: runner
tags: [completion-gate, dead-code, build-gate, stable]
affects:
  - "completion gate"
  - "stable deployment quality"
related-tasks: [ASS-753, ASS-770]
related-adrs: []
---

# Completion gate existed but was not wired into the review path

**What.** The ASS-753 CompletionGate code existed but was not wired, so non-building code could still reach stable.
**Why.** The gate implementation and the review execution path diverged; the presence of the class did not mean it was actually invoked.
**Workaround.** Verify pipeline execution records and build/test evidence before trusting a completion gate change.
**Long-term.** ASS-770 wired and deployed the gate.
