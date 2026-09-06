# Code not complete

The `3b-code-not-complete` lane parks a task after it exhausts its automatic pickup retry budget without reaching review. The runner keeps processing other work instead of repeatedly selecting the same task.

## What to do

Inspect the task's Result and Activity views for the last terminal outcome and missing implementation evidence. After correcting its prompt, dependencies, or repository context, send it back to Ready to try again. Send it to Preparation or Backlog when the task itself needs more work before another run.

The lane is hidden when empty. Repeated failures across several tasks indicate a systemic runner problem and can cause the project to leave automatic mode.
