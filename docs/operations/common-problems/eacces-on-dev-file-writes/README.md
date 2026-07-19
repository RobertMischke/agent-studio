---
id: eacces-on-dev-file-writes
title: "POSIX EACCES blocks Claude CLI from writing to agent-taskboard-dev files"
status: open
first-seen: 2026-05-27T06:31:06Z
last-seen: 2026-05-27T18:00:00Z
severity: blocker
category: permission
tags: [eacces, claude, posix, dev-checkout]
affects:
  - "agent-taskboard-dev/backend/**"
  - "Auto-pickup of tasks that touch backend files"
related-tasks: [human-decision-needed-feature-project-wiki-and-common-problems-library]
related-adrs: []
---

# eacces-on-dev-file-writes

**What.** Claude CLI's `Write` / `Edit` tool calls against files under `agent-taskboard-dev/` return POSIX `EACCES (permission denied)`, even though the same paths are writable from a regular git-bash or PowerShell session run by the same user.
**Why.** Not yet root-caused. Working hypothesis: a sibling process (running backend, file watcher, anti-virus, or a stale CLI session) holds an exclusive write lock on the file at the moment the CLI tool dispatches, and Windows surfaces the lock conflict as `EACCES` rather than `EBUSY`. Reproducer + handle-trace required before this can be confirmed.
**Workaround.** Stop the dev backend (`./api.sh stop` in the dev checkout) before letting the CLI edit backend files; retry the failed write after the lock clears. If `./api.sh status` shows the backend down and the error still occurs, restart the host machine.
**Long-term.** Capture handle-owner data with Process Explorer / `handle.exe` at the moment of failure; either narrow the AV exclusion or change the CLI's write path to a temp-then-rename pattern that survives a transient handle conflict.
