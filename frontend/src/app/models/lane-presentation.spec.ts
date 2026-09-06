import { ALL_TASK_STATES, TaskState } from './task.model';
import {
  LANE_PRESENTATIONS,
  laneDisplayName,
  lanePresentation,
  laneToneValue,
} from './lane-presentation';

describe('lane presentation', () => {
  it('defines a complete presentation for every canonical task state', () => {
    expect(Object.keys(LANE_PRESENTATIONS)).toEqual(ALL_TASK_STATES);
    for (const state of ALL_TASK_STATES) {
      const presentation = lanePresentation(state);
      expect(presentation).not.toBeNull();
      expect(presentation?.displayName).toBeTruthy();
      expect(presentation?.shortName).toBeTruthy();
      expect(presentation?.sentence).toBeTruthy();
      expect(presentation?.glyph).toBeTruthy();
      expect(presentation?.toneToken).toMatch(/^--studio-lane-/);
      expect(presentation?.docTopic).toMatch(/^lane-/);
    }
  });

  it('gives human review one name, sentence, and semantic tone', () => {
    const presentation = lanePresentation(TaskState.HumanReview);
    expect(presentation).toEqual(expect.objectContaining({
      displayName: 'Human review',
      shortName: 'Human review',
      sentence: 'Waiting for a human decision',
      toneToken: '--studio-lane-human-review',
      docTopic: 'lane-5-human-review',
    }));
    expect(laneToneValue(TaskState.HumanReview)).toBe('var(--studio-lane-human-review)');
  });

  it('resolves virtual and legacy states through their canonical presentation', () => {
    expect(laneDisplayName('2-ready-intake')).toBe('Preparation');
    expect(lanePresentation('2-ready-intake')?.docTopic).toBe('lane-2-ready');
    expect(laneDisplayName('4-review')).toBe('Post Processing');
    expect(laneDisplayName('1b-needs-human-review')).toBe('Human review');
    expect(laneDisplayName('unknown-lane')).toBe('unknown-lane');
  });
});
