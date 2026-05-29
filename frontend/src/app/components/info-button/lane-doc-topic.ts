/**
 * Lane state -> concept-doc topic for the lane-info modal.
 *
 * Each topic matches a committed file at <c>docs/concept-docs/{topic}.md</c>
 * served by <c>GET /api/concept-docs/{topic}</c>. Virtual sub-lanes
 * (e.g. <c>2-ready-intake</c>, <c>4-review</c>) collapse to their parent
 * lane's doc so the lane-info trigger reads the same prose everywhere a
 * lane is shown — board headers and the studio-shell active panel alike.
 *
 * Single source of truth: both the board lane header and the
 * task-status-card import {@link laneDocTopic} so the two surfaces can
 * never drift on which lanes have help.
 */
const LANE_DOC_TOPIC: Record<string, string> = {
  '0-backlog': 'lane-0-backlog',
  '1-preparation': 'lane-1-preparation',
  '1a-orchestrator-prep': 'lane-1a-orchestrator-prep',
  '1b-needs-human-review': 'lane-1b-needs-human-review',
  '2-ready': 'lane-2-ready',
  '2-ready-intake': 'lane-2-ready',
  '3-progress': 'lane-3-progress',
  '3a-failed-pickup': 'lane-3a-failed-pickup',
  '4-review': 'lane-4-auto-review',
  '4-auto-review': 'lane-4-auto-review',
  '5-human-review': 'lane-5-human-review',
  '6-completed': 'lane-6-completed',
  '7-archive': 'lane-7-archive',
};

/** Resolve a lane state to its concept-doc topic, or `null` when none exists. */
export function laneDocTopic(state: string | null | undefined): string | null {
  if (!state) return null;
  return LANE_DOC_TOPIC[state] ?? null;
}
