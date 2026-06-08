# {{title}}

{{prompt_text}}

---

{{mode_framing}}Context for this run (read after the task above):

- Working directory: `{{working_directory}}`
- Git repository for status/diff/commits: `{{repository_path}}`
- Job folder for task metadata and evidence: `{{job_folder}}`
- Task prompt path on disk: `{{prompt_path}}`
- Attachments folder (relative `attachments/<file>` paths resolve under the job folder):
{{attachments_list}}

Rules for this run:

- Work on this task only. Do not scan for or pick up other tasks.
- Do not move the job folder. Do not edit `state` in `job.json`. The application owns pickup, stop, continue, and state transitions.
- Do the implementation in the working directory above. Read or write task evidence in the job folder.
- Run git status and git diff in the repository path above when you need to inspect changes.
- Do not run `git commit`, `git push`, `git commit --amend`, or any branch/remote-mutating git command unless this individual task explicitly asks you to commit or push. The platform owns commit and push after the run.
- Place review-relevant screenshots / result files under the job folder's `results/` directory.
- Do not rely on hand-written `status.md`; the application regenerates it from logs.

Build-time observability (when your change affects product behavior):

- Preserve existing structured logs and event names; do not silently delete instrumentation while editing nearby code.
- For new meaningful behavior, emit structured logs or domain events with stable event names and useful error context, and add timing around expensive or user-visible paths when it would help future debugging or review.
- Skip instrumentation for tiny helpers, pure refactors, doc-only edits, and throwaway code. Observability is not a reason to bloat simple changes.

When you finish, end your reply with one of these tokens on its own line: `[[TASK_DONE]]`, `[[TASK_BLOCKED:<short reason>]]`, `[[TASK_NEEDS_INPUT:<short reason>]]`, or `[[TASK_NOOP]]` (rare). The orchestrator parses this token to decide what happens next.
