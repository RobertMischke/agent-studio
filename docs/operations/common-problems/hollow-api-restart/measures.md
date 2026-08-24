# Measures

Fix attempts and their status. Status vocabulary: `tried`, `applied`, `works`, `regressed`.

| Status | Date (UTC) | Measure | Owner | Outcome |
|---|---|---|---|---|
| works | 2026-08-24 | `stop` sweeps the port listener, the recorded PIDs, this checkout's `OrchestratorApi` processes and all their descendants, then verifies the port is free and exits non-zero if anything survives | AGT-2678 | Shapes 1 and 2 in [protocol.md](./protocol.md) are cleared; a failed stop can no longer be followed by a start |
| works | 2026-08-24 | `start` refuses a port owned by a process it did not launch, and proves the `/healthz` responder is inside its own launcher's process tree | AGT-2678 | Shape 3 fails loudly with the offending PID and command line instead of reporting success |
| works | 2026-08-24 | `restart` compares the PID plus process start time before and after, and fails when anything survives | AGT-2678 | A hollow restart is now a non-zero exit, not a success message |
| works | 2026-08-24 | `tools/api-restart-selfcheck.sh` as an executable regression test, hermetic plus a `--live` mode | AGT-2678 | 6 failures against the pre-fix script, 29 passes against the fixed one |
