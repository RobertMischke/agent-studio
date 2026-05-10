/**
 * Job-detail feature public API. Cycle 9h / ADR-0034.
 *
 * Sub-components (cli-config-card, command-deck, detail-header, git-pane,
 * hygiene-strip, log-overlay, pane-toggle-bar, prompt-pane, protocol-pane,
 * triage-panel) are private to the feature — only `JobDetailComponent` is
 * exported. Hygiene helpers + parsers used cross-feature stay exported.
 */
export { JobDetailComponent } from './job-detail';
export { HygieneStripComponent } from './components/hygiene-strip/hygiene-strip.component';
export { ProjectHygieneBadgeComponent } from './components/hygiene-strip/project-hygiene-badge.component';
export { ActivityLogViewComponent } from './components/activity-log-view';
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
