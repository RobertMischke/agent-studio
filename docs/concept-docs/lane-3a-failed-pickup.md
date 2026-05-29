# Failed Pickup

The `3a-failed-pickup` lane is the dead-letter for tasks that the orchestrator tried to start but that never produced a real run. After a configured number of empty attempts (default 3) — a CLI that exited before streaming any output, a run wedged behind a quota or auth wall — the card is moved here so it cannot permanently jam `3-progress`.

## What to do here

Investigate, then re-queue. A card here is usually the most restartable kind of failure: the work itself never began, so nothing is half-done. Check the activity log for why pickup failed (auth, quota, a bad prompt, a missing tool), fix the underlying cause, and send the card back to `2-ready` to try again. Cards do not retry themselves from this lane — that is the point. It exists so a repeatedly-failing task steps aside instead of blocking everything behind it.
