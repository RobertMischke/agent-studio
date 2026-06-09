# Escalated

The `5e-escalated` lane is the exception lane for tasks that need a human decision. A card lands here when auto-review cannot make a safe call on its own, for example because credentials are missing, the task conflicts with product direction, or the evidence shows a real unresolved choice.

## What to do here

Pick the next action deliberately:

- Continue (reissue) sends the task back through the runner with the open decision foregrounded.
- Resolve manually moves it to `5-human-review` once you have handled the decision outside the runner.
- Accept completes the task only after its latest task commit is integrated.
- Discard archives the card when the work should not continue.

This lane should stay small. Ordinary acceptable work belongs in `5-human-review`; escalation is only for decisions the system must not guess.
