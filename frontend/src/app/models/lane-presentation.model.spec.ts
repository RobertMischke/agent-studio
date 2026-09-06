import { describe, expect, it } from 'vitest';
import { ALL_TASK_STATES, TaskState } from './task.model';
import {
  LANE_PRESENTATIONS,
  laneName,
  lanePresentation,
  laneShortName,
  laneTone,
} from './lane-presentation.model';

describe('LANE_PRESENTATIONS', () => {
  it('defines complete, internally consistent presentation for every TaskState', () => {
    expect(Object.keys(LANE_PRESENTATIONS)).toEqual(ALL_TASK_STATES);
    for (const state of ALL_TASK_STATES) {
      const value = LANE_PRESENTATIONS[state];
      expect(value.state).toBe(state);
      expect(value.name.trim()).not.toBe('');
      expect(value.shortName.trim()).not.toBe('');
      expect(value.sentence.trim()).toMatch(/[.!?]$/);
      expect(value.toneToken).toMatch(/^--studio-lane-/);
      expect(value.glyph.trim()).not.toBe('');
      expect(value.docTopic).toMatch(/^lane-/);
    }
    expect(new Set(Object.values(LANE_PRESENTATIONS).map(value => value.toneToken)).size)
      .toBe(ALL_TASK_STATES.length);
  });

  it('uses one Human review identity across names, copy, tone, glyph, and docs', () => {
    expect(LANE_PRESENTATIONS[TaskState.HumanReview]).toEqual({
      state: TaskState.HumanReview,
      name: 'Human review',
      shortName: 'Human review',
      sentence: 'Waiting for a human decision.',
      toneToken: '--studio-lane-human-review',
      glyph: '👁️',
      docTopic: 'lane-5-human-review',
    });
  });

  it('resolves compatibility states without creating another presentation source', () => {
    expect(lanePresentation('4-review')).toBe(LANE_PRESENTATIONS[TaskState.AutoReview]);
    expect(lanePresentation('2-ready-intake')).toBe(LANE_PRESENTATIONS[TaskState.OrchestratorPrep]);
    expect(laneName(TaskState.HumanReview)).toBe(LANE_PRESENTATIONS[TaskState.HumanReview].name);
    expect(laneShortName(TaskState.HumanReview)).toBe(LANE_PRESENTATIONS[TaskState.HumanReview].shortName);
    expect(laneTone(TaskState.HumanReview)).toBe(`var(${LANE_PRESENTATIONS[TaskState.HumanReview].toneToken})`);
  });

  it('keeps unknown states readable and unstyled', () => {
    expect(lanePresentation('9-custom')).toBeNull();
    expect(laneName('9-custom')).toBe('9-custom');
    expect(laneTone('9-custom')).toBeNull();
  });
});
