# Root-cause protocol

1. Identify whether the process is finite work, a run-owned child, or a service
   intended to outlive the current session.
2. Inspect the process tree and determine which session, shell, unit, or runner
   owns the service.
3. Correlate the exit with session cleanup, cancellation, timeout, or a harness
   sweep before diagnosing an application crash.
4. Relaunch operator services through repository scripts or an OS-owned launch
   mechanism with file-based logs.
5. Stop services through their recorded process group, service unit, or
   repository lifecycle command instead of broad process-name matching.
