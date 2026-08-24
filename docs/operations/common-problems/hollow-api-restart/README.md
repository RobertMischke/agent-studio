---
id: hollow-api-restart
title: "api.sh restart reports success while the old backend keeps serving"
status: fixed
first-seen: 2026-08-23T10:34:00Z
last-seen: 2026-08-23T12:34:00Z
severity: blocker
category: cli
tags: [api-sh, restart, process-control, port, rollout, stale-code]
affects: [api.sh, scripts/update-stable.sh, docs/operations/setup/contributor-setup.md]
related-tasks: [AGT-2678]
related-adrs: [ADR-0044]
---

# hollow-api-restart

**What.** `api.sh restart` (and `stop`) printed `API stopped.` followed by `API is
successfully started and healthy!` while the previous OrchestratorApi process kept
running and serving. Observed in `agent-taskboard-stable`: a process from 12:34 was still
answering after two consecutive restarts. Consequences: rollouts silently served old
code, config reloads such as `project-settings.json` patches never took effect, the
watchdog's restart path was hollow, and an earlier zombie (PID 28116) held the DLLs so a
rebuild could not copy into `backend/bin`.

**Why.** Two blind spots, each sufficient on its own.
1. `dotnet run` is a launcher whose *child* owns the port. `stop` signalled a single PID,
   so one of the pair always survived. A surviving child kept serving; a surviving
   launcher kept the build output locked.
2. `start` accepted `/healthz` returning 200 as proof of its own success. An old process
   answers `/healthz` identically to a new one, so when it still owned the port the newly
   launched backend died with an address-in-use error and `start` reported the *old*
   process as the one it had started. Nothing compared the process before a restart with
   the process after it.

A health check cannot separate these cases by construction. The verification has to be
about process identity, not about liveness.

**Workaround.** None needed on a current checkout. Historically: `netstat`/`lsof` the port
by hand, kill the owning PID and every `dotnet`/`OrchestratorApi` process of that
checkout, confirm the port is free, then start.

**Long-term.** Fixed in AGT-2678. `api.sh` now verifies each step: `stop` sweeps the port
listener plus every `OrchestratorApi` process of that checkout and proves the port is
free; `start` refuses a port owned by a process it did not launch and confirms the
`/healthz` responder is the process it started; `restart` asserts that the PID and the
process start time both changed. `tools/api-restart-selfcheck.sh` reproduces both failure
shapes and fails against any `api.sh` that does not satisfy the contract, so the fix
cannot silently regress.
