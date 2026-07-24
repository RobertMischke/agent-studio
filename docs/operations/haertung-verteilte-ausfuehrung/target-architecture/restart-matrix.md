# Restart matrix & decoupled lifecycles

| Actor dies | What survives | Who notices, how | Recovery |
|---|---|---|---|
| Task Server | everything (truth on disk) | all clients: API errors → retry/backoff | systemd restarts; clients reconnect |
| Engine | all runs & queues (server state) | Task Server last-seen goes stale | fresh Engine resumes queue; nothing orphaned |
| Runner (host) | delivered commits, salvage branches | leases expire server-side | work re-offered; salvage picked up on next claim |
| Studio | everything | user sees offline guard | reload |
| A library release | consumers pin versions | nothing breaks silently | bump when ready |

**Contrast — today (one process):** backend restart kills CLI child processes and orphans in-flight runs (observed 23–24 Jul: recovery scans only `2-ready`, so `3-progress` victims need operator requeues). The actor split removes this class entirely: runs are server state, execution is leased, and every lease has an owner that can die safely.
