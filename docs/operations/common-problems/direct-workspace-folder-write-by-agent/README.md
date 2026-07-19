---
id: direct-workspace-folder-write-by-agent
title: "Agent writes to workspace task folders directly instead of using the API"
status: open
first-seen: 2026-05-27T00:00:00Z
last-seen: 2026-05-27T23:59:00Z
severity: major
category: state-machine
tags: [workspace, api-first, task-folders, state-machine]
affects:
  - "agent-taskboard-workspace/projects/**"
  - "Task scanner and in-memory index consistency"
related-tasks: [human-decision-needed-feature-project-wiki-and-common-problems-library]
related-adrs: []
---

# direct-workspace-folder-write-by-agent

**What.** An agent directly creates, edits, moves, or deletes task folders under the workspace instead of using the task API.
**Why.** Direct filesystem edits bypass the scanner, transition service, cache invalidation, and event broadcasts.
**Workaround.** Stop and replace the action with the API route from `.agents/skills/task-api/SKILL.md`.
**Long-term.** Keep the AGENTS rule prominent and add task-access guardrails where possible.
