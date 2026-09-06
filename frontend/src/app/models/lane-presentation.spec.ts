import { describe, expect, it } from 'vitest';
import { ALL_TASK_STATES, TaskState } from './task.model';
import {
  LANE_PRESENTATIONS,
  laneName,
  lanePresentation,
  laneShortName,
  laneToneValue,
} from './lane-presentation';

describe('lane presentation', () => {
  it('defines a complete presentation for every canonical task state', () => {
    expect(Object.keys(LANE_PRESENTATIONS)).toEqual([...ALL_TASK_STATES]);
    for (const state of ALL_TASK_STATES) {
      const presentation = lanePresentation(state);
      expect(presentation).not.toBeNull();
      expect(presentation?.state).toBe(state);
      expect(presentation?.name).toBeTruthy();
      expect(presentation?.shortName).toBeTruthy();
      expect(presentation?.sentence).toBeTruthy();
      expect(presentation?.toneToken).toMatch(/^--studio-lane-/);
      expect(presentation?.glyph).toBeTruthy();
      expect(presentation?.docTopic).toMatch(/^lane-/);
      expect(presentation?.groupKey).toBeTruthy();
    }
  });

  it('gives human review one name and one tone on every lookup path', () => {
    const presentation = lanePresentation(TaskState.HumanReview)!;
    expect(presentation.name).toBe('Human review');
    expect(presentation.shortName).toBe(presentation.name);
    expect(presentation.sentence).toBe('Waiting for a human decision');
    expect(presentation.toneToken).toBe('--studio-lane-human-review');
    expect(laneName(TaskState.HumanReview)).toBe(presentation.name);
    expect(laneShortName(TaskState.HumanReview)).toBe(presentation.name);
    expect(laneToneValue(TaskState.HumanReview)).toBe('var(--studio-lane-human-review)');
  });

  it('normalises compatibility display states through the same catalogue', () => {
    expect(lanePresentation('4-review')).toBe(lanePresentation(TaskState.AutoReview));
    expect(lanePresentation('2-ready-intake')).toBe(lanePresentation(TaskState.Preparation));
    expect(lanePresentation('1b-needs-human-review')).toBe(lanePresentation(TaskState.HumanReview));
    expect(lanePresentation('not-a-lane')).toBeNull();
  });
});
