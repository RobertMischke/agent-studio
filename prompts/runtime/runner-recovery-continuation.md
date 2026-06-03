# {{title}}

{{prompt_text}}

---

User follow-up (latest direction on top of the task above):

{{user_followup}}

---

{{mode_framing}}You are picking up this task. The previous CLI session was lost, so you do not have the conversation history; treat the task body above as your authoritative starting point and the follow-up (if present) as the user's latest direction on top of it. The task body is **not** background context - it is the work you are here to do.

Do **not** reply "I'll wait for your request" or "standing by". The user already gave you both the task and any follow-up. If you cannot proceed after reading the evidence, end with `[[TASK_BLOCKED:<reason>]]` and explain why.

Reconstruct surrounding context only as needed:

1. Re-read `prompt.md` at `{{prompt_path}}` if you need the canonical version with formatting.
2. Skim the last 100 lines of `logs/cli-output.log` if it exists.
3. Run git status and git diff in `{{repository_path}}` before editing anything.

Run context:

- Working directory: `{{working_directory}}`
- Git repository: `{{repository_path}}`
- Job folder: `{{job_folder}}`
- Attachments folder (relative `attachments/<file>` paths resolve under the job folder):
{{attachments_list}}

Rules: same guardrails as a fresh run. Work on this task only; do not move the job folder; do not edit `state` in `job.json`; do not scan for or start another task. The application owns pickup, stop, continue, and state transitions.

Build-time observability (when your change affects product behavior):

- Preserve existing structured logs and event names; do not silently delete instrumentation while editing nearby code.
- For new meaningful behavior, emit structured logs or domain events with stable event names and useful error context, and add timing around expensive or user-visible paths when it would help future debugging or review.
- Skip instrumentation for tiny helpers, pure refactors, doc-only edits, and throwaway code. Observability is not a reason to bloat simple changes.

When you finish, end your reply with one of these tokens on its own line: `[[TASK_DONE]]`, `[[TASK_BLOCKED:<short reason>]]`, `[[TASK_NEEDS_INPUT:<short reason>]]`, or `[[TASK_NOOP]]`.
