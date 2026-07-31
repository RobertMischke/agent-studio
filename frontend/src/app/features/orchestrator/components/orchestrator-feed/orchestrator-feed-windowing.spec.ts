import { describe, expect, it } from 'vitest';
import { OrchestratorFeedWindow } from './orchestrator-feed-windowing';

describe('OrchestratorFeedWindow', () => {
  it('bounds a 500-event feed to the latest 100 and pages older rows', () => {
    const window = new OrchestratorFeedWindow();
    const entries = Array.from({ length: 500 }, (_, index) => index);

    expect(window.slice(entries)).toHaveLength(100);
    expect(window.remaining(entries.length, window.slice(entries).length)).toBe(400);

    window.loadOlder(400);
    expect(window.slice(entries)).toHaveLength(200);
  });

  it('keeps the current history mounted when live entries arrive away from newest', () => {
    const window = new OrchestratorFeedWindow();
    window.sync('all\u0000signal', 500, true);

    expect(window.sync('all\u0000signal', 503, false)).toBe(3);
    expect(window.size()).toBe(103);
  });

  it('resets the bounded window when the filter scope changes', () => {
    const window = new OrchestratorFeedWindow();
    window.loadOlder(400);
    expect(window.size()).toBe(200);

    window.sync('project-a\u0000alerts', 25, true);
    expect(window.size()).toBe(100);
  });
});
