/**
 * Job-detail feature public API. Cycle 9h / ADR-0034.
 *
 * Sub-components (cli-config-card, command-deck, detail-header, git-pane,
 * hygiene-strip, log-overlay, pane-toggle-bar, prompt-pane, protocol-pane)
 * are private to the feature. The epic rollup pane is exported for the
 * app-level epic master/detail overlay because that overlay must preserve the
 * current task context while showing the epic shell.
 * The lane-action primary button + overflow menu now render inside
 * `detail-header`; the catalogue is headless in `state/triage-actions.model.ts`.
 * Hygiene helpers + parsers used cross-feature stay exported.
 */
export { TaskDetailComponent } from './task-detail';
export { EpicRollupPaneComponent } from './components/epic-rollup-pane/epic-rollup-pane.component';
export { TaskSelectionService } from './state/task-selection.service';
export { TriageController } from './state/triage-controller.service';
export { LanePagerService } from './state/lane-pager.service';
export {
  overflowActionsFor,
  primaryActionFor,
  laneLabelFor,
  mergeAcceptViewFor,
  LANE_LABELS,
  type MergeAcceptView,
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
} from './activity-log';
export {
  classifyOutcome,
  type OutcomeAssessment,
  type QuickReply,
} from './components/agent-outcome.util';
