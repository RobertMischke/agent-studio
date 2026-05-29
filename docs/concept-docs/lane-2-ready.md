# Ready

The `2-ready` lane is the queue of refined, runnable tasks waiting for a coding agent. A card here has everything an agent needs: a clear prompt, scope, and acceptance criteria. This is the lane the orchestrator pulls from — but only after `3-progress` is clear, because the product deliberately runs one task at a time per project.

## How pickup works

Order matters. Within this lane the orchestrator takes work oldest-first, so the card at the top is the next to run. The lane also splits into sub-groups: tasks a human marked ready, and tasks the orchestrator's intake has cleared. A card you marked ready still passes an intake check before a runner claims it. To change what runs next, reorder the lane rather than waiting — the top of `2-ready` is the on-deck task once a slot frees up.
