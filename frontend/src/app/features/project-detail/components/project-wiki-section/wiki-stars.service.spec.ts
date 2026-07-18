import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { computed } from '@angular/core';
import { WikiStarEntry, WikiStarsService } from './wiki-stars.service';

const DEMO_KEY = 'atp.projectWikiStars.v1.Demo';

function clearStarStorage(): void {
  for (const key of Object.keys(localStorage)) {
    if (key.startsWith('atp.projectWikiStars.v1.')) localStorage.removeItem(key);
  }
}

describe('WikiStarsService', () => {
  beforeEach(() => clearStarStorage());
  afterEach(() => clearStarStorage());

  it('stars and unstars per project, persisting to localStorage and updating the signal', () => {
    const service = new WikiStarsService();
    // A computed downstream consumer sees every mutation live (signal-based store).
    const live = computed(() => service.entries('Demo').map(entry => entry.relPath));
    expect(live()).toEqual([]);

    service.toggle('Demo', 'concepts/overview.md', 'Concept overview');
    expect(service.isStarred('Demo', 'concepts/overview.md')).toBe(true);
    expect(live()).toEqual(['concepts/overview.md']);
    // Persisted under the per-project key with label + starredAt.
    const stored = JSON.parse(localStorage.getItem(DEMO_KEY)!) as WikiStarEntry[];
    expect(stored).toHaveLength(1);
    expect(stored[0]).toMatchObject({ relPath: 'concepts/overview.md', label: 'Concept overview' });
    expect(typeof stored[0].starredAt).toBe('string');
    // Stars are project-scoped.
    expect(service.isStarred('Other', 'concepts/overview.md')).toBe(false);
    expect(service.entries('Other')).toEqual([]);

    // Toggling again unstars, empties the signal, and removes the storage key.
    service.toggle('Demo', 'concepts/overview.md', 'Concept overview');
    expect(service.isStarred('Demo', 'concepts/overview.md')).toBe(false);
    expect(live()).toEqual([]);
    expect(localStorage.getItem(DEMO_KEY)).toBeNull();
  });

  it('lists the most recently starred entry first', () => {
    const service = new WikiStarsService();
    service.star('Demo', 'one.md', 'Eins');
    service.star('Demo', 'two.md', 'Zwei');
    service.star('Demo', 'three.md', 'Drei');
    expect(service.entries('Demo').map(entry => entry.relPath)).toEqual(['three.md', 'two.md', 'one.md']);
  });

  it('hydrates stored stars newest-first and ignores corrupt payloads', () => {
    localStorage.setItem(DEMO_KEY, JSON.stringify([
      { relPath: 'old.md', label: 'Alt', starredAt: '2026-07-01T08:00:00.000Z' },
      { relPath: 'new.md', label: 'Neu', starredAt: '2026-07-15T08:00:00.000Z' },
      { relPath: '', label: 'kaputt', starredAt: '2026-07-16T08:00:00.000Z' }, // invalid: dropped
      'garbage', // invalid: dropped
    ]));
    localStorage.setItem('atp.projectWikiStars.v1.Broken', 'not-json');

    const service = new WikiStarsService();
    expect(service.entries('Demo').map(entry => entry.relPath)).toEqual(['new.md', 'old.md']);
    expect(service.entries('Demo')[0].label).toBe('Neu');
    expect(service.entries('Broken')).toEqual([]);
  });
});
