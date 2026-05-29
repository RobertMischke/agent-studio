/**
 * Job-detail feature public API. Cycle 9h / ADR-0034.
 *
 * Sub-components (cli-config-card, command-deck, detail-header, git-pane,
 * hygiene-strip, log-overlay, pane-toggle-bar, prompt-pane, protocol-pane)
 * are private to the feature — only `TaskDetailComponent` is exported.
 * The lane-action primary button + overflow menu now render inside
 * `detail-header`; the catalogue is headless in `state/triage-actions.model.ts`.
 * Hygiene helpers + parsers used cross-feature stay exported.
 */
export { TaskDetailComponent } from './task-detail';
export { TaskSelectionService } from './state/task-selection.service';
export { TriageController } from './state/triage-controller.service';
export {
  overflowActionsFor,
  primaryActionFor,
  laneLabelFor,
  LANE_LABELS,
  type TriageActionPayload,
  type TriageButton,
} from './state/triage-actions.model';
export { HygieneStripComponent } from './components/hygiene-strip/hygiene-strip/hygiene-strip.component';
export { ProjectHygieneBadgeComponent } from './components/hygiene-strip/project-hygiene-badge/project-hygiene-badge.component';
export { ActivityLogViewComponent } from './components/activity-log-view/activity-log-view';
export {
  parseActivityLog,
  buildConversationTurns,
  type ActivityLogGroup,
  type ActivityLogKind,
} from './components/activity-log.parser';
export {
  classifyOutcome,
  type OutcomeAssessment,
  type QuickReply,
} from './components/agent-outcome.util';
