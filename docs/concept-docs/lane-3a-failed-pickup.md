# Failed Pickup (retired)

The `3a-failed-pickup` lane has been retired. It was a dead-letter lane for tasks that the orchestrator tried to start but that never produced a real run. ADR-0051 eliminated it: a task failing pickup is always a bug in the pickup path, not a state a user should have to triage.

There is no longer a `3a-failed-pickup` lane, banner, toast, or amber dot in the board. No live code path routes a folder into it.

## Where those folders go now

See [failed-pickup-elimination.md](failed-pickup-elimination.md) for the full cause-by-cause routing table. In short:

- A folder with a `job.json` is a real task. An interrupted run is requeued to `2-ready`; a task that genuinely cannot be started after a bounded number of attempts is escalated to `5-human-review`. A real task is never dead-lettered.
- A folder with no `job.json` is debris. It is deleted when the real job is provably elsewhere, otherwise archived to `7-archive` with its evidence intact.
- A broken CLI (spawn failure) is infrastructure, not a task fault. The task waits in `2-ready` with a clear status and the runner pauses so it does not spin; it resumes when a human fixes the CLI.

Historical `3a-failed-pickup` folders are drained on boot: real tasks to `2-ready`, debris to `7-archive`.
