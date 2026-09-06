import { describe, expect, it } from 'vitest';
import { ALL_TASK_STATES, TaskState } from './task.model';
import {
  LANE_PRESENTATIONS,
  laneDisplayName,
  lanePresentation,
  laneShortName,
  laneTone,
} from './lane-presentation';

describe('lane presentation', () => {
  it('defines complete presentation metadata for every canonical TaskState', () => {
    expect(Object.keys(LANE_PRESENTATIONS)).toEqual(expect.arrayContaining([...ALL_TASK_STATES]));
    expect(Object.keys(LANE_PRESENTATIONS)).toHaveLength(ALL_TASK_STATES.length);

    for (const state of ALL_TASK_STATES) {
      const presentation = LANE_PRESENTATIONS[state];
      expect(presentation.displayName).toBeTruthy();
      expect(presentation.shortName).toBeTruthy();
      expect(presentation.sentence).toBeTruthy();
      expect(presentation.glyph).toBeTruthy();
      expect(presentation.toneToken).toMatch(/^--studio-lane-/);
      expect(presentation.docTopic).toMatch(/^lane-/);
    }
  });

  it('keeps the human-review name, sentence, tone, glyph, and docs together', () => {
    expect(LANE_PRESENTATIONS[TaskState.HumanReview]).toEqual({
      displayName: 'Human review',
      shortName: 'Human review',
      sentence: 'Waiting for a human decision',
      toneToken: '--studio-lane-human-review',
      glyph: '👁️',
      docTopic: 'lane-5-human-review',
    });
  });

  it('resolves compatibility lanes without creating another presentation source', () => {
    expect(lanePresentation('2-ready-intake')).toBe(LANE_PRESENTATIONS[TaskState.OrchestratorPrep]);
    expect(lanePresentation('4-review')).toBe(LANE_PRESENTATIONS[TaskState.AutoReview]);
    expect(laneDisplayName(TaskState.HumanReview)).toBe('Human review');
    expect(laneShortName(TaskState.HumanReview)).toBe('Human review');
    expect(laneTone(TaskState.HumanReview)).toBe('var(--studio-lane-human-review)');
    expect(lanePresentation('unknown')).toBeNull();
  });
});
