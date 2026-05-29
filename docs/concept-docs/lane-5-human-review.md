# Human Review

The `5-human-review` lane holds finished runs that are waiting for your judgement. The agent has done the work and reported an outcome; auto-review may already have looked at it. What is left is the call a person should make: is this actually done, and is it good enough to accept?

## What to do here

Review the evidence and decide. Open the card to read the activity log, the diff, and any review verdicts, then either accept the work — sending it to `6-completed` — or send it back for another pass when something is missing or wrong. This lane is intentionally a human gate: nothing advances out of it automatically. A card sits here until you act, so the queue here is your personal to-review list, not something the orchestrator will clear on its own.
