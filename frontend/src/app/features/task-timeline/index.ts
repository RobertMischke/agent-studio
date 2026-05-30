/**
 * Task-timeline feature public API (ADR-0049 / ASS-566).
 *
 * The per-task event ledger (`logs/timeline.jsonl`) and the derived
 * completion-loop summary the Overview attempt-cycle indicator + Timeline
 * tab render. The wire shape mirrors the backend `TimelineEvent.cs`.
 */
export type {
  TaskTimelineEvent,
  CompletionLoopVerdict,
  CompletionLoopState,
  VerdictTone,
} from './models/task-timeline.model';
export {
  TIMELINE_KIND,
  deriveCompletionLoop,
  verdictLabel,
  verdictGlyph,
  verdictTone,
} from './models/task-timeline.model';
export { TaskTimelinePaneComponent } from './components/task-timeline-pane/task-timeline-pane.component';
export { CompletionLoopIndicatorComponent } from './components/completion-loop-indicator/completion-loop-indicator.component';
