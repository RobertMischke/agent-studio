---
id: circuit-breaker-3x-failure-cascade
title: "Auto-continuous flips to manual after three consecutive same-task failures"
status: open
first-seen: 2026-05-27T00:00:00Z
last-seen: 2026-05-27T23:59:00Z
severity: blocker
category: runner
tags: [circuit-breaker, runner, auto-continuous, manual-mode]
affects:
  - "Project runner auto-continuous mode"
  - "Long unattended task batches"
related-tasks: [human-decision-needed-feature-project-wiki-and-common-problems-library]
related-adrs: []
---

# circuit-breaker-3x-failure-cascade

**What.** A project flips from auto-continuous to manual after repeated failures on the same task.
**Why.** The infrastructure breaker is protecting the queue from spinning on an unavailable CLI or irrecoverable failure.
**Workaround.** Inspect the active task and runner status, fix the underlying CLI or task blocker, then resume via the verified runner resume path.
**Long-term.** Keep breaker trips typed and visible so operators can tell infrastructure failure from genuine task ambiguity.
