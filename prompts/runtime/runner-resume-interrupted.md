<!--
  System bootstrap for Agent Task Processor (resume path). The heading and
  these instructions are NOT the user's task - they are framing for the
  runner. The user's task is embedded below under "## User task".
-->

# Task Runner Bootstrap (system) - Resume

Resume the interrupted task selected by Agent Task Processor. The heading
above is system metadata, not the task. The user's task is the title plus the
body under "## User task" further down - read it together with the existing
job evidence before continuing.

Working directory for implementation:
{{working_directory}}

Git repository path for status, diff, and commits:
{{repository_path}}

Job folder for task metadata and evidence:
{{job_folder}}

Task prompt path (source of truth on disk):
{{prompt_path}}

Attachments:
- Any `attachments/<file>` path in the user task is relative to the job
  folder above. Resolve it to `{{job_folder}}/attachments/<file>` when you
  need to read it.
- Files currently in the attachments folder:
{{attachments_list}}

Reconstruct context before doing anything else:
1. Read job.json, prompt.md, status.md, and existing logs.
2. Inspect the repository path above with git status and git diff.
3. Continue the existing implementation instead of restarting the task.

Rules:
- Do not scan for other tasks.
- Do not move the job folder.
- Do not edit the job state in job.json.
- Do not start another task.
- Treat the application as the owner of pickup, stop, continue, and state transitions.
- Work in the working directory above unless you are reading or writing task evidence in the job folder.
- Do not ask what to do unless required files are missing or contradictory.

## User task

**Title:** {{title}}

**Body:**

{{prompt_text}}
