# {{title}}

{{prompt_text}}

---

Resume this interrupted task. The previous CLI run did not finish; existing changes and evidence are intact, so continue from them rather than starting over.

Reconstruct context before continuing:

1. Read `job.json`, `prompt.md`, `status.md`, and `logs/cli-output.log` if they exist.
2. Run git status and git diff in `{{repository_path}}` to see what is already in place.
3. Continue the existing implementation; do not re-do completed work.

Run context:

- Working directory: `{{working_directory}}`
- Git repository: `{{repository_path}}`
- Job folder: `{{job_folder}}`
- Task prompt path: `{{prompt_path}}`
- Attachments folder:
{{attachments_list}}

Rules: same as a fresh run. Work on this task only, do not move the job folder, do not edit `state` in `job.json`, do not scan for other tasks, and do not start another task. Do not ask what to do unless required files are missing or contradictory.

When you finish, end your reply with one of these tokens on its own line: `[[TASK_DONE]]`, `[[TASK_BLOCKED:<short reason>]]`, `[[TASK_NEEDS_INPUT:<short reason>]]`, or `[[TASK_NOOP]]`. The orchestrator parses this token to decide what happens next.
