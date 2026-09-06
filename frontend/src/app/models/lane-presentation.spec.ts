import { describe, expect, it } from 'vitest';
import { ALL_TASK_STATES, TaskState } from './task.model';
import {
  LANE_PRESENTATIONS,
  PRESENTED_TASK_STATES,
  laneDisplayName,
  lanePresentation,
  laneToneValue,
} from './lane-presentation';

describe('lane presentation', () => {
  it('covers every canonical TaskState with complete presentation metadata', () => {
    expect(PRESENTED_TASK_STATES).toEqual(ALL_TASK_STATES);
    expect(Object.keys(LANE_PRESENTATIONS)).toEqual(expect.arrayContaining([...ALL_TASK_STATES]));

    for (const state of ALL_TASK_STATES) {
      const presentation = lanePresentation(state);
      expect(presentation, state).not.toBeNull();
      expect(presentation?.displayName, state).toBeTruthy();
      expect(presentation?.shortName, state).toBeTruthy();
      expect(presentation?.sentence, state).toBeTruthy();
      expect(presentation?.toneToken, state).toMatch(/^--studio-lane-/);
      expect(presentation?.glyph, state).toBeTruthy();
      expect(presentation?.docTopic, state).toMatch(/^lane-/);
    }
  });

  it('gives human review one name, sentence, tone, glyph, and help topic', () => {
    expect(LANE_PRESENTATIONS[TaskState.HumanReview]).toEqual({
      displayName: 'Human review',
      shortName: 'Human review',
      sentence: 'Waiting for a human decision',
      toneToken: '--studio-lane-human-review',
      glyph: '👁️',
      docTopic: 'lane-5-human-review',
    });
    expect(laneDisplayName(TaskState.HumanReview)).toBe('Human review');
    expect(laneToneValue(TaskState.HumanReview)).toBe('var(--studio-lane-human-review)');
  });

  it('normalizes virtual and compatibility keys without copying presentation', () => {
    expect(lanePresentation('2-ready-intake')).toMatchObject({
      displayName: 'Preparation',
      glyph: '🛂',
      docTopic: 'lane-2-ready',
    });
    expect(lanePresentation('4-review')).toBe(LANE_PRESENTATIONS[TaskState.AutoReview]);
    expect(lanePresentation('unknown')).toBeNull();
    expect(laneDisplayName('unknown')).toBe('unknown');
  });
});
