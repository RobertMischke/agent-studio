---
id: codex-silent-finish
title: "Codex run finishes silently without a terminal sentinel"
status: open
first-seen: 2026-06-05T00:00:00Z
last-seen: 2026-06-05T00:00:00Z
severity: major
category: cli
tags: [codex, silent-finish, missing-terminal-sentinel, watchdog]
affects:
  - "Codex CLI runs"
  - "runner outcome classification"
related-tasks: [ASS-744, ASS-755, ASS-734, ASS-780]
related-adrs: []
---

# Codex run finishes silently without a terminal sentinel

**What.** Codex sometimes stops in the middle of work without emitting the required terminal sentinel, leaving the runner to infer completion from silence.
**Why.** Observed runs cluster around watchdog-timeout investigations and apply-patch mismatch paths; treat the silent-completion detector as recovery, not proof of success.
**Workaround.** Inspect the last tool call, open items, and generated diff before accepting a silent Codex finish.
**Long-term.** ASS-780 tracks the runner-side fix so Codex silent exits are surfaced as suspicious and routed through completion/evidence gates.
