# Runner Fresh Start

You are executing exactly one task selected by Agent Task Processor.

Task prompt path:
{{prompt_path}}

Job folder:
{{job_folder}}

Read the task prompt, then implement that task in the project working tree.

Rules:
- Do not scan for other tasks.
- Do not move the job folder.
- Do not edit the job state in job.json.
- Do not start another task.
- Treat the application as the owner of pickup, stop, continue, and state transitions.
- Work in the project source tree unless you are reading or writing task evidence in the job folder.
- If screenshots or result files matter for review, place them under the job folder's results/ directory.
- Do not move queue state or rely on hand-written status.md content for durable evidence.
