import { describe, expect, it } from 'vitest';
import { groupReviewJobs } from './review-grouping.util';
import { TaskInfo } from '../../../models/task.model';

function job(id: string, verdict: TaskInfo['orchestratorVerdict'] = null): TaskInfo {
  return {
    id,
    taskKey: `ws::${id}`,
    title: id,
    state: '4-review',
    order: 1,
    agent: 'claude',
    createdAt: '2026-05-05T12:00:00Z',
    watchPath: 'ws',
    projectName: 'demo',
    folderPath: '/tmp',
    lastActivity: '2026-05-05T12:00:00Z',
    orchestratorVerdict: verdict
  } as TaskInfo;
}

describe('groupReviewJobs', () => {
  it('routes verdict-bearing jobs into the orchestrator sub-section', () => {
    const groups = groupReviewJobs([
      job('reissued',   'reissue'),
      job('escalated',  'escalate'),
      job('accepted',   'accept'),
      job('clean-done')
    ]);

    const [orchestrator, human] = groups;
    expect(orchestrator.kind).toBe('orchestrator');
    expect(orchestrator.jobs.map(j => j.id)).toEqual(['reissued', 'escalated', 'accepted']);

    expect(human.kind).toBe('human');
    expect(human.jobs.map(j => j.id)).toEqual(['clean-done']);
  });

  it('always returns both sub-sections, even when one is empty', () => {
    const groups = groupReviewJobs([job('a', 'reissue')]);
    expect(groups).toHaveLength(2);
    expect(groups[0].jobs).toHaveLength(1);
    expect(groups[1].jobs).toHaveLength(0);
  });

  it('preserves source ordering within each sub-section', () => {
    const groups = groupReviewJobs([
      job('a'),
      job('b', 'escalate'),
      job('c'),
      job('d', 'reissue')
    ]);
    expect(groups[0].jobs.map(j => j.id)).toEqual(['b', 'd']);
    expect(groups[1].jobs.map(j => j.id)).toEqual(['a', 'c']);
  });
});
