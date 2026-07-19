# MVP presentation storyboard and shot list

The medium is a live 1920x1080 slide deck. Product stills carry the main story.
Two optional silent loops add motion, and one optional narrated recording is a
backup if the live presentation cannot be delivered.

Follow the [presentation capture runbook](../operations/setup/presentation-capture.md)
for the demo-data guard, reset, tool settings, and review checklist.

## Ordered deck story

| Order | Required | Source tool | Capture | Slide caption | Output filename |
|---:|---|---|---|---|---|
| 01a | Yes | Playwright | Cross-lane board, dark theme | One operating view from idea to shipped result. | `01-board-overview--dark--real.png` |
| 01b | Yes | Playwright | Cross-lane board, light theme | The same operating model works in both themes. | `01-board-overview--light--real.png` |
| 02a | Yes | Playwright | DEMO-5 execution detail, dark theme | Every task keeps its prompt, run state, and evidence together. | `02-task-execution-detail--dark--real.png` |
| 02b | Yes | Playwright | DEMO-5 execution detail, light theme | Execution remains readable without leaving the task. | `02-task-execution-detail--light--real.png` |
| 03a | Yes | Playwright | DEMO-5 quality-grade review, dark theme | Automated review turns agent output into a human decision. | `03-review-evidence--dark--real.png` |
| 03b | Yes | Playwright | DEMO-5 quality-grade review, light theme | Findings stay attached to the work they qualify. | `03-review-evidence--light--real.png` |
| 04a | Yes | Playwright | Seeded orchestrator conversation, dark theme | Steer the portfolio in context, without losing the audit trail. | `04-orchestrator-conversation--dark--real.png` |
| 04b | Yes | Playwright | Seeded orchestrator conversation, light theme | The orchestrator explains the next best move. | `04-orchestrator-conversation--light--real.png` |
| 05a | Yes | Playwright | Project token economy, dark theme | Token use is visible and attributable, not ambient mystery spend. | `05-token-economy--dark--real.png` |
| 05b | Yes | Playwright | Project token economy, light theme | Cost and usage stay part of the operating loop. | `05-token-economy--light--real.png` |
| 06a | Yes | Playwright | Project knowledge tree, dark theme | Project knowledge travels with execution. | `06-project-knowledge--dark--real.png` |
| 06b | Yes | Playwright | Project knowledge tree, light theme | Agents and operators work from the same context. | `06-project-knowledge--light--real.png` |
| 07 | Optional | ScreenToGif | Open DEMO-5 from the board, reveal review evidence, return to board; dark theme, silent | From portfolio signal to review evidence in one motion. | `07-task-to-review-loop--dark--real.gif` |
| 08 | Optional | ScreenToGif | Open orchestrator, read the seeded recommendation, move to token usage; light theme, silent | Steering connects priorities to operating cost. | `08-steering-to-tokens-loop--light--real.gif` |
| 09 | Optional backup | OBS Studio | Narrated 60 to 90 second traversal of rows 01 through 06; dark theme, microphone only, no webcam | Agent Studio makes delegated work visible, steerable, and reviewable. | `09-mvp-walkthrough--dark--real.mp4` |

## Presenter sequence

1. Start wide on the board. Explain the lane model and that every card is
   deterministic demo data.
2. Open DEMO-5. Connect the task prompt and run history to the product claim
   that work is observable.
3. Show the quality-grade evidence. Pause on the concrete medium finding and
   explain why human review remains explicit.
4. Open the orchestrator conversation. Position it as portfolio steering with
   a durable transcript, not a separate chat toy.
5. Move to token economy. Show that orchestration cost is attributable.
6. End on project knowledge. Land the message that context, execution, and
   evidence share one workspace.

Use one theme per live slide according to the deck design, while retaining the
paired alternate-theme still for review and last-minute deck changes. Keep
captions in the slide so the same product media can be reused without editing.
