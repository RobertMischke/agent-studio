---
id: services-killed-by-harness-sweep
title: "Persistent services die when a session cleanup sweep reaps their process tree"
status: open
first-seen: 2026-07-09T00:00:00Z
last-seen: 2026-07-09T00:00:00Z
severity: major
category: runner
tags: [services, process-tree, session-lifecycle, windows, linux]
affects:
  - "backend and frontend development services"
  - "runner hosts and persistent tunnels"
related-tasks: [ASS-1761]
related-adrs: []
---

# Persistent services die when a session cleanup sweep reaps their process tree

**What.** A backend, frontend server, tunnel, watcher, or runner daemon started
as a session-managed background task may die at session exit, cancellation, a
timeout, or a harness cleanup sweep.

**Why.** Background status does not change ownership. The service remains in
the session's process tree, and cleanup correctly reaps that tree. This launch
shape is appropriate for finite builds and tests but not for services that
must outlive the session.

**Workaround.** Choose ownership before launch:

| Workload | Owner | Launch shape |
|---|---|---|
| Build, test, finite probe, bounded wait loop | Current task or session | Foreground or session-managed background process |
| Local service needed after the task ends | Operator or OS | Detached process with file-based logs |
| Unattended Linux service or persistent tunnel | `systemd` | Unit with restart and health policy |
| Child process created for one coding run | Product runner | Runner-owned process tree, torn down with the run |

On Windows, use `Start-Process` with a hidden window, an explicit working
directory, and stdout and stderr redirected to files. On Linux, prefer a
`systemd` unit. For temporary operator-owned services, use `setsid`, disconnect
stdin, and redirect output to files. Avoid broad `pkill -f` patterns, especially
patterns that also occur in the command issuing the kill.

OS detachment is not a substitute for product process management. Children of
a coding run must remain runner-owned so cancellation can reap the whole tree.
The stricter dev-backend rule in the root `AGENTS.md` still applies.

**Long-term.** Persistent infrastructure should be owned by an OS service
manager with explicit restart, shutdown, logging, and health policies.
