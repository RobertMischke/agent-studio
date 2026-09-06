import { TaskState, type TaskStateKey } from './task.model';

/** UI metadata for one canonical task lane. */
export interface LanePresentation {
  /** Full lane name used by board columns and descriptive surfaces. */
  displayName: string;
  /** Compact lane name used by chips and selectors. */
  shortName: string;
  /** Standalone sentence describing what the lane means now. */
  sentence: string;
  /** Theme-aware semantic custom property that owns the lane hue. */
  toneToken: `--studio-lane-${string}`;
  /** Lane identity glyph used wherever the lane is listed. */
  glyph: string;
  /** Concept-doc topic served by the lane information endpoint. */
  docTopic: `lane-${string}`;
}

/**
 * The only source of user-facing lane names, tones, glyphs, and help topics.
 * Keep this exhaustive when adding a TaskState so every projection changes
 * together.
 */
export const LANE_PRESENTATIONS: Readonly<Record<TaskStateKey, LanePresentation>> = {
  [TaskState.Backlog]: {
    displayName: 'Backlog',
    shortName: 'Backlog',
    sentence: 'Captured and waiting to be scheduled',
    toneToken: '--studio-lane-backlog',
    glyph: '🗒️',
    docTopic: 'lane-0-backlog',
  },
  [TaskState.Preparation]: {
    displayName: 'In Preparation',
    shortName: 'Preparation',
    sentence: 'Waiting for intake and preparation',
    toneToken: '--studio-lane-preparation',
    glyph: '📋',
    docTopic: 'lane-1-preparation',
  },
  [TaskState.OrchestratorPrep]: {
    displayName: 'Orchestrator prep',
    shortName: 'Orchestrator prep',
    sentence: 'Preparing the task for orchestration',
    toneToken: '--studio-lane-orchestrator-prep',
    glyph: '🛂',
    docTopic: 'lane-1a-orchestrator-prep',
  },
  [TaskState.Ready]: {
    displayName: 'Ready',
    shortName: 'Ready',
    sentence: 'Ready for pickup',
    toneToken: '--studio-lane-ready',
    glyph: '📦',
    docTopic: 'lane-2-ready',
  },
  [TaskState.Progress]: {
    displayName: 'In Progress',
    shortName: 'In Progress',
    sentence: 'A run is executing the task',
    toneToken: '--studio-lane-progress',
    glyph: '🔵',
    docTopic: 'lane-3-progress',
  },
  [TaskState.FailedPickup]: {
    displayName: 'Failed pickup',
    shortName: 'Failed pickup',
    sentence: 'Waiting for pickup recovery',
    toneToken: '--studio-lane-failed-pickup',
    glyph: '⚠️',
    docTopic: 'lane-3a-failed-pickup',
  },
  [TaskState.CodeNotComplete]: {
    displayName: 'Code not complete',
    shortName: 'Code not complete',
    sentence: 'Waiting for the implementation to be completed',
    toneToken: '--studio-lane-code-not-complete',
    glyph: '🚧',
    docTopic: 'lane-3b-code-not-complete',
  },
  [TaskState.AutoReview]: {
    displayName: 'Post Processing',
    shortName: 'Post Processing',
    sentence: 'Running automated review gates',
    toneToken: '--studio-lane-auto-review',
    glyph: '🤖',
    docTopic: 'lane-4-auto-review',
  },
  [TaskState.Escalated]: {
    displayName: 'Escalated',
    shortName: 'Escalated',
    sentence: 'Waiting for operator attention',
    toneToken: '--studio-lane-escalated',
    glyph: '⚠️',
    docTopic: 'lane-5e-escalated',
  },
  [TaskState.HumanReview]: {
    displayName: 'Human review',
    shortName: 'Human review',
    sentence: 'Waiting for a human decision',
    toneToken: '--studio-lane-human-review',
    glyph: '👁️',
    docTopic: 'lane-5-human-review',
  },
  [TaskState.Completed]: {
    displayName: 'Delivered',
    shortName: 'Delivered',
    sentence: 'Delivered and accepted',
    toneToken: '--studio-lane-completed',
    glyph: '🟢',
    docTopic: 'lane-6-completed',
  },
  [TaskState.Archive]: {
    displayName: 'Archive',
    shortName: 'Archive',
    sentence: 'Archived outside the active workflow',
    toneToken: '--studio-lane-archive',
    glyph: '🗄️',
    docTopic: 'lane-7-archive',
  },
};

const PRESENTATION_ALIASES: Readonly<Record<string, TaskStateKey>> = {
  '1b-needs-human-review': TaskState.HumanReview,
  '2-ready-intake': TaskState.OrchestratorPrep,
  '4-review': TaskState.AutoReview,
};

/** Resolve canonical and supported compatibility lane keys. */
export function lanePresentation(state: string | null | undefined): LanePresentation | null {
  if (!state) return null;
  const canonical = PRESENTATION_ALIASES[state] ?? state;
  return LANE_PRESENTATIONS[canonical as TaskStateKey] ?? null;
}

export function laneDisplayName(state: string | null | undefined): string {
  return lanePresentation(state)?.displayName ?? state ?? '';
}

export function laneShortName(state: string | null | undefined): string {
  return lanePresentation(state)?.shortName ?? state ?? '';
}

/** CSS value suitable for an Angular style binding. */
export function laneTone(state: string | null | undefined): string | null {
  const token = lanePresentation(state)?.toneToken;
  return token ? `var(${token})` : null;
}
