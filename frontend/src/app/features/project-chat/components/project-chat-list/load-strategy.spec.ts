import { describe, expect, it } from 'vitest';
import { decideLoadAction, formatLoadedSummary } from './load-strategy';

describe('decideLoadAction', () => {
  const base = {
    loadedCount: 0,
    hasMoreOlder: true,
    isLoading: false,
    isNearTop: true,
    threshold: 1000,
  };

  it('returns no-op when there is no more older history', () => {
    expect(decideLoadAction({ ...base, hasMoreOlder: false })).toBe('no-op');
  });

  it('returns no-op while a request is already in flight', () => {
    expect(decideLoadAction({ ...base, isLoading: true })).toBe('no-op');
  });

  it('shows the step-load panel once loaded count reaches the threshold', () => {
    expect(decideLoadAction({ ...base, loadedCount: 1000 })).toBe('show-panel');
    expect(decideLoadAction({ ...base, loadedCount: 4711 })).toBe('show-panel');
  });

  it('shows the panel even if the user is not near the top', () => {
    // Past threshold, scroll-driven loading is off entirely — the panel
    // is the only path forward.
    expect(decideLoadAction({ ...base, loadedCount: 1500, isNearTop: false })).toBe('show-panel');
  });

  it('continues backfill below the threshold when near top', () => {
    expect(decideLoadAction({ ...base, loadedCount: 200 })).toBe('continue-backfill');
  });

  it('returns no-op below the threshold when not near top', () => {
    expect(decideLoadAction({ ...base, loadedCount: 200, isNearTop: false })).toBe('no-op');
  });
});

describe('formatLoadedSummary', () => {
  it('includes total when known', () => {
    const s = formatLoadedSummary(1000, 47238, '2026-04-15T10:00:00Z');
    expect(s).toContain('1,000');
    expect(s).toContain('47,238');
    expect(s).toContain('messages');
  });

  it('omits total when unknown', () => {
    const s = formatLoadedSummary(50, null, null);
    expect(s).toContain('50');
    expect(s).not.toContain(' of ');
  });

  it('includes oldest date when provided', () => {
    const s = formatLoadedSummary(100, 200, '2026-04-15T10:00:00Z');
    expect(s).toContain('Oldest loaded');
  });
});
