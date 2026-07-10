# Persistent Service Lifecycle

Status: maintained operational guidance for starting services used by the
taskboard, runners, and test environments.

A command launched as part of an agent or supervisor session belongs to that
session's lifetime. Cleanup sweeps, cancellation, timeouts, or session exit may
terminate its process tree. This is correct for finite commands such as builds
and tests, but it makes the same launch mechanism unsuitable for a backend,
frontend dev server, tunnel, watcher, or runner daemon that must survive the
session.

## Choose ownership before launch

| Workload | Owner | Launch shape |
|---|---|---|
| Build, test, finite probe, bounded wait loop | Current task or session | Foreground or session-managed background process |
| Local service needed after the task ends | Operator or OS | Detached process with file-based logs |
| Unattended Linux service or persistent tunnel | `systemd` | Unit with restart and health policy |
| Child process created for one coding run | Product runner | Runner-owned process tree, torn down with the run |

Do not confuse OS detachment with product process management. A coding run's
children must remain runner-owned so cancellation can reap the full tree. Only
operator services whose lifetime intentionally exceeds the session should be
detached.

## Platform guidance

- On Windows, use `Start-Process` with a hidden window, an explicit working
  directory, and stdout/stderr redirected to files.
- On Linux, prefer a `systemd` unit for unattended operation. For a temporary
  operator-owned service, start a new session with `setsid`, disconnect stdin,
  and redirect stdout/stderr to files.
- Use repository lifecycle scripts where they exist. They carry the expected
  ports, environment, health checks, and shutdown behavior.
- Avoid broad process-name matching for shutdown. Stop the recorded process,
  process group, unit, or repository-managed service instead.

The dev backend remains subject to the stricter repository rule in
[`AGENTS.md`](../../../AGENTS.md): it may be started only through the stable
Playwright fixture. This page does not grant an alternate startup path.

## Related

- [Runner domain map](../../domains/runner.md)
- [Task execution and log architecture](../../concepts/task-execution-and-log-architecture.md)
- [Linux runner host](../../operations/setup/linux-runner-host.md)
- [Remote runner persistent connection](../../operations/setup/remote-runner-persistent-connection.md)

## Living knowledge log

- 2026-07-11: Migrated the service-versus-session lifetime invariant from
  private agent memory into the project wiki.
