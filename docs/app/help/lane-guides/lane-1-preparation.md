# Preparation

The `1-preparation` lane holds tasks that are being shaped into something a coding agent can actually run. A card here has a goal but may still be missing the detail an agent needs: a clear prompt, acceptance criteria, scope boundaries, or the right attachments. Preparation is where that gap gets closed.

## What happens here

Refinement, not execution. The orchestrator does not start a CLI run from this lane; instead the prompt is sharpened until the task is unambiguous. Some preparation is done by hand; some is handed to the orchestrator's intake step, which drafts and tightens the task for you. When a task is well-formed it moves to `2-ready`; when shaping surfaces a question only a person should answer, it is escalated to `5-human-review`.
