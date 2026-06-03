# Root-Cause Protocol

## Current Understanding

Claude CLI tool calls can fail with POSIX `EACCES` while direct shell writes by the same Windows user still work. The likely causes are transient file locks, process ownership, anti-virus inspection, or a CLI-specific write path that does not tolerate Windows locking behavior.

## Reproducer To Capture

1. Confirm dev backend state with `./api.sh status`.
2. Trigger a Claude edit against a backend file that previously failed.
3. If `EACCES` appears, capture the affected path, process list, and handle owner before retrying.

## Evidence To Add

- Exact stderr snippet from the failed CLI run.
- Affected file path.
- Backend/frontend watcher state at failure time.
- Whether retry after stopping dev backend succeeds.
