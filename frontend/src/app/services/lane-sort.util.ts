/**
 * F35: per-lane sort-strategy metadata shared by the project-settings
 * dropdown and the kanban lane-header indicator. The strategy ids mirror
 * the backend `LaneSortStrategies` constants exactly; keep them in sync.
 */

export interface LaneSortStrategyMeta {
  /** Strategy id sent to the backend (empty string clears the override). */
  value: string;
  /** Human label for the settings dropdown. */
  label: string;
  /** Compact glyph for the lane-header indicator. */
  icon: string;
  /** One-line explanation used in tooltips. */
  hint: string;
}

const META: Record<string, LaneSortStrategyMeta> = {
  manual: {
    value: 'manual',
    label: 'Manual',
    icon: '≡',
    hint: 'Manual order — drag cards to reorder.',
  },
  'newest-first': {
    value: 'newest-first',
    label: 'Newest first',
    icon: '↓',
    hint: 'Newest task on top.',
  },
  'oldest-first': {
    value: 'oldest-first',
    label: 'Oldest first',
    icon: '↑',
    hint: 'Oldest task on top (FIFO).',
  },
  'last-activity': {
    value: 'last-activity',
    label: 'Last activity',
    icon: '◷',
    hint: 'Most recently active task on top.',
  },
  // Internal runner-only strategy; never offered in the settings dropdown,
  // but the board may still resolve to it for the auto-pickup lane.
  'pickup-priority': {
    value: 'pickup-priority',
    label: 'Pickup priority',
    icon: '⚡',
    hint: 'Auto-pickup priority order.',
  },
};

/** Strategies offered in the project-settings dropdown, in display order. */
export const USER_VISIBLE_LANE_SORT_STRATEGIES: readonly LaneSortStrategyMeta[] = [
  META['manual'],
  META['newest-first'],
  META['oldest-first'],
  META['last-activity'],
];

export function laneSortStrategyMeta(strategy: string | null | undefined): LaneSortStrategyMeta {
  if (strategy && META[strategy]) return META[strategy];
  return META['manual'];
}

export function isManualStrategy(strategy: string | null | undefined): boolean {
  return strategy === 'manual';
}

export interface SortableLaneMeta {
  state: string;
  label: string;
  icon: string;
}

/**
 * Lanes surfaced in the project-settings sort-strategy section, in board
 * order. The internal `3a-failed-pickup` lane is intentionally omitted — it
 * is a short-lived bounce lane the operator does not curate by hand.
 */
export const SORTABLE_LANES: readonly SortableLaneMeta[] = [
  { state: '0-backlog', label: 'Backlog', icon: '🗒️' },
  { state: '1-preparation', label: 'In Preparation', icon: '📋' },
  { state: '1a-orchestrator-prep', label: 'Orch Prep', icon: '🤖' },
  { state: '1b-needs-human-review', label: 'Needs Clarification', icon: '🚩' },
  { state: '2-ready', label: 'Ready', icon: '📦' },
  { state: '3-progress', label: 'In Progress', icon: '🔵' },
  { state: '4-auto-review', label: 'Auto Review', icon: '🤖' },
  { state: '5-human-review', label: 'Review', icon: '👁️' },
  { state: '6-completed', label: 'Completed', icon: '🟢' },
  { state: '7-archive', label: 'Archive', icon: '🗄️' },
];

/**
 * Map a board display-state to the backend lane key its sort strategy is
 * stored under. Most are 1:1; the Ready lane is split into a human-ready
 * and an orchestrator-intake sub-lane on the board, but both share the
 * single `2-ready` strategy.
 */
export function displayStateToLaneKey(state: string): string {
  if (state === '2-ready-intake') return '2-ready';
  return state;
}
