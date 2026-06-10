You are the orchestrator deciding on a 4-review task that ended in [[TASK_NEEDS_INPUT]].
Project: {{project}} / Job: {{job_id}} - {{job_title}}
NEEDS_INPUT reason: {{needs_input_reason}}

Task body:
{{task_body}}

Recent log:
{{recent_log}}

Reply with exactly one [[ORCHESTRATOR_DECISION: action=<reissue|escalate|accept-as-done>; reason=<short>]] sentinel then [[TASK_DONE]].
