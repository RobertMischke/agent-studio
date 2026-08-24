# Root-cause protocol

## Reproducers

Both shapes are reproduced hermetically by `tools/api-restart-selfcheck.sh`, which copies
`api.sh` into a throwaway checkout and puts a stub `dotnet` on PATH that mirrors the real
launcher/child process shape. No .NET build is required.

1. **Orphan that outlived its launcher.** Start, then kill only the launcher. The child
   keeps the port and keeps answering `/healthz`.
2. **Non-listening process of the same checkout.** The app that stopped serving but never
   exited. A port-only stop is blind to it by construction; it is the shape that held the
   DLLs.
3. **Healthy stranger on the port.** Any process answering `/healthz` 200 that this
   checkout did not launch.

## What the old script did

`cmd_stop` resolved a single PID (pid file, else the first port listener), signalled it,
swept remaining port listeners, then printed `API stopped.` unconditionally. There was no
post-condition check, and no notion of a process belonging to the checkout, so shape 2
was invisible.

`cmd_start` polled `/healthz` and returned success on the first 200. Against shape 3 it
took the `API is already running and healthy (PID: <stranger>)` branch; against shape 1
the newly launched `dotnet run` died with an address-in-use error while the loop happily
read the old process's 200.

`restart` was `cmd_stop; cmd_start` with no assertion and no exit-code propagation, so a
failed stop still ran a start.

## Verification of the fix

Running the self-check against the pre-fix `api.sh` (`git show HEAD~1:api.sh`) fails 6
named checks and exits 1, including:

```
FAIL: stop reported success while PID 1411008 from this checkout kept running
FAIL: start reported success while a foreign process owned the port:
      ... API is already running and healthy (PID: 1387022).
```

The second line is the incident symptom verbatim. Against the fixed script the same run
passes 29 checks and exits 0.

## Why a health check is the wrong instrument

`/healthz` answers a liveness question. The failure is an identity question: *is the
process serving this port the one I just started?* Any check built on the response body
or status code is satisfied by the old process. The fix therefore compares PID paired
with process start time (PID numbers are reused), and establishes ownership by walking
the launcher's process tree.

## Scope of the sweep

The sweep is deliberately narrow, because an over-broad one would cause a worse incident
than it repairs. A process is only swept when its command line names
`<checkout>/backend/OrchestratorApi.csproj` or `<checkout>/backend/bin/`, and never when
it looks like a build or test run (`.Tests`, `vstest`, `testhost`, `MSBuild.dll`,
`/nodemode:`). `dotnet test` on `backend.Tests/OrchestratorApi.Tests.csproj` spawns
vstest and testhost children carrying both the marker and the checkout path; sweeping
those would kill a test run started from an unrelated terminal. A process of the same
checkout that listens on a *different* port is left alone as well, which keeps the
isolated worktree test stack (ASS-1715) safe. Sibling-checkout isolation is asserted by
the self-check.
