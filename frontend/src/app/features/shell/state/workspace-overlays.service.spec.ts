import { beforeEach, describe, expect, it } from 'vitest';
import { WorkspaceOverlaysService } from './workspace-overlays.service';

describe('WorkspaceOverlaysService retired project-sources route', () => {
  beforeEach(() => {
    history.replaceState(null, '', window.location.pathname + window.location.search);
  });

  it('migrates a stale deep-link to the settings overview', () => {
    history.replaceState(null, '', '/#/workspace/settings/project-sources');
    const service = new WorkspaceOverlaysService();

    service.syncFromHash();

    expect(service.settingsOpen()).toBe(true);
    expect(service.section()).toBe('overview');
    expect(window.location.hash).toBe('#/workspace/settings');
  });
});

/**
 * Regression for the hybrid-hash collision (operator report 2026-07-21):
 * `#filters=...&/workspace/settings`. The settings deep link and the board's
 * `filters=` segment are independent hash segments (url-hash.util.ts) and must
 * coexist: opening/closing settings never disturbs the filter segment, and a
 * composite hash still opens the recognised section.
 */
describe('WorkspaceOverlaysService coexistence with a board filter segment', () => {
  const FILTERS = 'filters=projects%3AAgent%20Studio%20Marketing';

  beforeEach(() => {
    history.replaceState(null, '', window.location.pathname + window.location.search);
  });

  it('opening a section keeps an existing filters segment', () => {
    history.replaceState(null, '', `/#${FILTERS}`);
    const service = new WorkspaceOverlaysService();

    service.open('overview');

    expect(window.location.hash).toBe(`#/workspace/settings&${FILTERS}`);
  });

  it('recognises the section from a composite hash regardless of segment order', () => {
    history.replaceState(null, '', `/#${FILTERS}&/workspace/settings/task-server`);
    const service = new WorkspaceOverlaysService();

    service.syncFromHash();

    expect(service.settingsOpen()).toBe(true);
    expect(service.section()).toBe('task-server');
  });

  it('closing settings removes only its own route, leaving the filters segment', () => {
    history.replaceState(null, '', `/#/workspace/settings&${FILTERS}`);
    const service = new WorkspaceOverlaysService();
    service.syncFromHash();
    expect(service.settingsOpen()).toBe(true);

    service.close();

    expect(service.settingsOpen()).toBe(false);
    expect(window.location.hash).toBe(`#${FILTERS}`);
  });
});
