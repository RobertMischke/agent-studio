# Runner Resume Interrupted

Resume the interrupted task selected by Agent Task Processor.

Job folder:
{{job_folder}}

Reconstruct context before doing anything else:
1. Read job.json, prompt.md, status.md, and existing logs.
2. Inspect the project working tree with git status and git diff.
3. Continue the existing implementation instead of restarting the task.

Rules:
- Do not scan for other tasks.
- Do not move the job folder.
- Do not edit the job state in job.json.
- Do not start another task.
- Treat the application as the owner of pickup, stop, continue, and state transitions.
- Do not ask what to do unless required files are missing or contradictory.
