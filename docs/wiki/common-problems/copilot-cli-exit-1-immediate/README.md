---
id: copilot-cli-exit-1-immediate
title: "Copilot CLI exits with code 1 immediately on pickup"
status: open
first-seen: 2026-05-27T00:00:00Z
last-seen: 2026-05-27T23:59:00Z
severity: major
category: cli
tags: [copilot, cli, exit-1, round-robin]
affects:
  - "Copilot task pickup"
  - "Round-robin CLI selection"
related-tasks: [human-decision-needed-feature-project-wiki-and-common-problems-library]
related-adrs: []
---

# copilot-cli-exit-1-immediate

**What.** Copilot exits with code 1 almost immediately after pickup.
**Why.** The CLI can be unavailable, unauthenticated, misconfigured, or invoked with an unsupported command shape.
**Workaround.** Pause affected auto-pickup, verify Copilot CLI health manually, and route urgent tasks to a healthy CLI.
**Long-term.** Make startup probes and quota/auth failures block Copilot selection before task pickup.
