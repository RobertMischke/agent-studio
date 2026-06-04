import { beforeEach, describe, expect, it } from 'vitest';
import { ProjectOverlaysService } from './project-overlays.service';

/**
 * Deep-link anchor contract for the per-project orchestrator feed
 * (`#/project/<slug>/feed`). The feed used to open with no URL hash, so a
 * bookmark or reload could not reproduce the open feed. These tests lock:
 *   - opening stamps the singular `#/project/<slug>/feed` anchor,
 *   - closing clears it,
 *   - a fresh service reconciles the open feed from the hash on boot,
 *   - the anchor is distinct from the plural `#/projects/` shell prefix,
 *   - a shell-stacked feed (no hash) is not yanked shut by a non-feed hash.
 */
describe('ProjectOverlaysService · orch-feed deep-link anchor', () => {
  const watchPaths = [{ name: 'Agent Task Processor' }, { name: 'Runbook' }];

  beforeEach(() => {
    history.replaceState(null, '', window.location.pathname + window.location.search);
  });

  it('stamps #/project/<slug>/feed when the feed opens', () => {
    const svc = new ProjectOverlaysService();
    svc.openOrchFeed('Agent Task Processor');
    expect(svc.orchFeedProject()).toBe('Agent Task Processor');
    expect(window.location.hash).toBe('#/project/agent-task-processor/feed');
  });

  it('uses the singular prefix so it cannot collide with the #/projects/ shell prefix', () => {
    const svc = new ProjectOverlaysService();
    svc.openOrchFeed('Runbook');
    expect(window.location.hash.startsWith('#/projects/')).toBe(false);
    expect(window.location.hash).toBe('#/project/runbook/feed');
  });

  it('clears the feed anchor when the feed closes', () => {
    const svc = new ProjectOverlaysService();
    svc.openOrchFeed('Runbook');
    svc.closeOrchFeed();
    expect(svc.orchFeedProject()).toBeNull();
    expect(window.location.hash).toBe('');
  });

  it('reproduces the open feed from the hash on boot (reload / bookmark)', () => {
    history.replaceState(null, '', '/#/project/runbook/feed');
    const fresh = new ProjectOverlaysService();
    expect(fresh.orchFeedProject()).toBeNull();
    fresh.syncFeedFromHash(watchPaths);
    expect(fresh.orchFeedProject()).toBe('Runbook');
  });

  it('leaves the feed signal alone until watch-paths resolve the slug', () => {
    history.replaceState(null, '', '/#/project/runbook/feed');
    const svc = new ProjectOverlaysService();
    svc.syncFeedFromHash([]);
    expect(svc.orchFeedProject()).toBeNull();
    svc.syncFeedFromHash(watchPaths);
    expect(svc.orchFeedProject()).toBe('Runbook');
  });

  it('closes a hash-opened feed when its hash is dropped', () => {
    history.replaceState(null, '', '/#/project/runbook/feed');
    const svc = new ProjectOverlaysService();
    svc.syncFeedFromHash(watchPaths);
    expect(svc.orchFeedProject()).toBe('Runbook');

    history.replaceState(null, '', window.location.pathname + window.location.search);
    svc.syncFeedFromHash(watchPaths);
    expect(svc.orchFeedProject()).toBeNull();
  });

  it('does not yank a shell-stacked feed (opened without a hash) shut on a non-feed hash', () => {
    const svc = new ProjectOverlaysService();
    svc.projectShellName.set('Runbook');
    svc.openFeedFromShell();
    expect(svc.orchFeedProject()).toBe('Runbook');

    // A shell-hash reconciliation fires while the feed is stacked.
    history.replaceState(null, '', '/#/projects/runbook/overview');
    svc.syncFeedFromHash(watchPaths);
    expect(svc.orchFeedProject()).toBe('Runbook');
  });
});
