---
id: folder-move-lock-recurrence
title: "Open handles or orphan processes block task folder moves"
status: mitigated
first-seen: 2026-06-05T00:00:00Z
last-seen: 2026-06-05T00:00:00Z
severity: major
category: filesystem
tags: [folder-move, file-lock, orphan-process, windows]
affects:
  - "task lane transitions"
  - "workspace filesystem"
related-tasks: [ASS-759]
related-adrs: []
---

# Open handles or orphan processes block task folder moves

**What.** Folder moves fail for one to two hours because a log handle or orphan process still holds a file under the task folder.
**Why.** A process or stream remains attached after the run, so Windows refuses the lane folder move.
**Workaround.** Restarting the backend or killing the stale process has mitigated the lock.
**Long-term.** Ensure runner and log readers close handles promptly, and add diagnostics that identify the owning process.
