import { lanePresentation } from '../../models/lane-presentation';

/**
 * Lane state -> concept-doc topic for the lane-info modal.
 *
 * Each topic matches a committed file at <c>docs/app/help/lane-guides/{topic}.md</c>
 * served by <c>GET /api/concept-docs/{topic}</c>. Virtual sub-lanes
 * (e.g. <c>2-ready-intake</c>, <c>4-review</c>) collapse to their parent
 * lane's doc so the lane-info trigger reads the same prose everywhere a
 * lane is shown — board headers and the studio-shell active panel alike.
 *
 * Single source of truth: both the board lane header and the
 * task-status-card import {@link laneDocTopic} so the two surfaces can
 * never drift on which lanes have help.
 */
/** Resolve a lane state to its concept-doc topic, or `null` when none exists. */
export function laneDocTopic(state: string | null | undefined): string | null {
  return lanePresentation(state)?.docTopic ?? null;
}
