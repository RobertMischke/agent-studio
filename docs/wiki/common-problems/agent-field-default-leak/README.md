---
id: agent-field-default-leak
title: "New tasks show human in agent field instead of the actual model"
status: fixed
first-seen: 2026-05-27T00:00:00Z
last-seen: 2026-05-27T23:59:00Z
severity: minor
category: ui
tags: [agent-field, defaults, cli-type, card-label]
affects:
  - "Task creation"
  - "Kanban card agent labels"
related-tasks: [human-decision-needed-prevent-agent-human-leakage-into-new-jobs]
related-adrs: []
---

# agent-field-default-leak

**What.** Newly created tasks displayed `agent: human` even when auto-pickup would run a CLI agent.
**Why.** The persisted job data did not materialize the effective owner/client defaults at create time.
**Workaround.** None needed for new tasks after the fix; inspect legacy tasks if labels look stale.
**Long-term.** Keep `agent` and `cliType` synchronized through the canonical mutation path.
