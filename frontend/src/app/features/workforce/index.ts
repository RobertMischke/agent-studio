/**
 * Workforce feature public API.
 *
 * The workforce module owns the chat surfaces' role-attribution layer:
 * the role catalogue, the deterministic author/kind/refs → role mapper,
 * the chat-phase grouping helper, and the two presentational components
 * (`RoleBadgeComponent`, `PhaseSummaryListComponent`) that the project
 * chat and task chat surfaces both render.
 *
 * Per ADR-0034, cross-feature imports MUST go through this barrel.
 */
export {
  ROLE_CATALOGUE,
  resolveRole,
  getRole,
  type RoleAttributionInput,
  type WorkforceRole,
  type WorkforceRoleId,
} from './models/workforce-role';
export {
  groupIntoPhases,
  groupIntoSuperPhases,
  buildSummary,
  type ChatPhase,
  type PhaseInputMessage,
  type SuperPhase,
  type SuperPhaseGroupingOptions,
} from './models/chat-phase';
export { RoleBadgeComponent } from './components/role-badge/role-badge.component';
export {
  PhaseSummaryListComponent,
  formatPhaseRange,
} from './components/phase-summary-list/phase-summary-list.component';
