/**
 * The lifecycle-phase label logic now lives in the shared source of truth at
 * `src/app/services/lifecycle-phase.util.ts` so the task-detail title chip and
 * the board task-card pill (`buildPhaseBadge`) can never drift on wording or
 * elapsed formatting. This module forwards to it, keeping the detail view's
 * co-located import stable.
 */
export { lifecyclePhaseLabel } from '../../../../../services/lifecycle-phase.util';
