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
