{{user_followup}}

---

PRIMARY INSTRUCTION: the request above is from the user and is the only thing you need to act on. The previous CLI session for this task was lost and cannot be resumed; conversation history is gone. Use the original task description and the job folder evidence below as background context only.

Do **not** reply "I'll wait for your request" or "standing by" - the user already gave you the request above. If you cannot perform it after looking at the evidence, end with `[[TASK_BLOCKED:<reason>]]` and explain why.

Original task (reference only):

**Title:** {{title}}

**Body:**

{{prompt_text}}

Reconstruct surrounding context only as needed:

1. Read `prompt.md` at `{{prompt_path}}`.
2. Skim the last 100 lines of `logs/cli-output.log` if that file exists.
3. Run git status and git diff in `{{repository_path}}` before editing anything.

Run context:

- Working directory: `{{working_directory}}`
- Git repository: `{{repository_path}}`
- Job folder: `{{job_folder}}`
- Attachments folder (relative `attachments/<file>` paths resolve under the job folder):
{{attachments_list}}

Rules: work on this task only, do not move the job folder, do not edit `state` in `job.json`, do not scan for or start another task. The application owns pickup, stop, continue, and state transitions.

When you finish, end your reply with one of these tokens on its own line: `[[TASK_DONE]]`, `[[TASK_BLOCKED:<short reason>]]`, `[[TASK_NEEDS_INPUT:<short reason>]]`, or `[[TASK_NOOP]]`.
