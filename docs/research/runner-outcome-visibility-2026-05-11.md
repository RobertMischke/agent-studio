# Runner Outcome Visibility

This note documents the runner outcome categories that replaced the broad
`heuristicfallback` bucket for common post-run failure shapes.

## Categories

| Category | Meaning | First orchestrator action | Exhausted action |
|---|---|---|---|
| `permission-blocked` | The CLI reported that permission was denied and no user permission request could be made. | One soft intervention asks the agent to continue with available permissions and emit a terminal sentinel. | Stop and route the task to Human Review with a concrete permission category. |
| `watchdog-timeout` | The watchdog killed a run after it stopped producing progress. | None. A killed process is already terminal. | Stop and route the task to Human Review with a concrete watchdog category. |
| `missing-terminal-sentinel` | The reply looked useful or completed, but did not include the expected terminal sentinel. | One soft intervention asks for exactly one sentinel. | Accept or stop with the concrete missing-sentinel category, depending on the run outcome. |
| `classifier-unknown` | The output had text, but the classifier could not map it to a known shape. | None. | Stop or accept with `classifier-unknown`, not `heuristicfallback`. |
| `heuristic-done` | Compatibility path for completed-looking replies that still need the old heuristic. | None. | Accept with a visible compatibility category. |

## Visibility Surfaces

The durable source stays `logs/cli-output.log`. No parallel storage path is
introduced.

The backend derives `JobInfo.outcomeIssue` from the latest categorized
orchestrator line in that log. The board card and Protocol header render this
field directly, and the Protocol header opens a modal with the category,
severity, last-seen time, explanation, and raw summary.

Project-level visibility uses the existing Agent Message Bus. New bus topics
include `permission-blocked`, `watchdog-timeout`, `missing-terminal-sentinel`,
`classifier-unknown`, `heuristic-done`, and `soft-intervention`. The project
Observability panel groups them in an Outcome attention strip and links each
chip back to the latest matching bus message.

## Policy Notes

The runner still separates classification from action:

1. `AgentOutcomeAnalyzer` labels the observed output.
2. `RunOutcomePolicy` decides whether to accept, stop, or issue a one-shot
   intervention.
3. `ProjectRunner` applies the action, writes the chat log, mirrors to the bus,
   and routes terminal permission/watchdog cases to Human Review.

This keeps the agent reply parser narrow while giving the orchestrator room to
advance recoverable cases once.
