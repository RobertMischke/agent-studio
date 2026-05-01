# Runner Recovery Continuation

Continue this task. The previous CLI session was lost and cannot be resumed.

Job folder:
{{job_folder}}

Reconstruct context before doing anything else:
1. Read job.json, prompt.md, and status.md.
2. Read the last 200 lines of logs/cli-output.log if that file exists.
3. Run git status and git diff in the project working tree.

Then continue with the user's follow-up below. Treat this as a continuation, not a new task. Keep existing changes and evidence.

User follow-up:
{{user_followup}}

Rules:
- Do not scan for other tasks.
- Do not move the job folder.
- Do not edit the job state in job.json.
- Do not start another task.
- Treat the application as the owner of pickup, stop, continue, and state transitions.
