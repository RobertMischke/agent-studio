import { describe, expect, it } from 'vitest';
import { CodeReviewActivityStore } from './code-review-activity.store';

/**
 * Unit spec for the in-memory code-review activity registry. The store is
 * the single source of truth the kanban card reads to decide whether to
 * render its "code review…" badge, so the key composition and the
 * mark/clear lifecycle are the load-bearing contract.
 */
describe('CodeReviewActivityStore', () => {
  it('composes a stable key from watchPath + id and isolates different tasks', () => {
    const a = CodeReviewActivityStore.key('C:/projects/foo', 'job-1');
    const b = CodeReviewActivityStore.key('C:/projects/foo', 'job-2');
    const c = CodeReviewActivityStore.key('C:/projects/bar', 'job-1');

    expect(a).toBe(CodeReviewActivityStore.key('C:/projects/foo', 'job-1'));
    expect(a).not.toBe(b);
    expect(a).not.toBe(c);
  });

  it('tolerates a null/undefined watchPath without colliding ids', () => {
    expect(CodeReviewActivityStore.key(null, 'job-1')).toBe('::job-1');
    expect(CodeReviewActivityStore.key(undefined, 'job-1')).toBe('::job-1');
  });

  it('marks a key running, reports it, then clears it', () => {
    const store = new CodeReviewActivityStore();
    const key = CodeReviewActivityStore.key('C:/projects/foo', 'job-1');

    expect(store.isRunning(key)).toBe(false);

    store.markRunning(key);
    expect(store.isRunning(key)).toBe(true);
    // Another task is unaffected.
    expect(store.isRunning(CodeReviewActivityStore.key('C:/projects/foo', 'job-2'))).toBe(false);

    store.clear(key);
    expect(store.isRunning(key)).toBe(false);
  });

  it('is idempotent: double mark then a single clear leaves it not running', () => {
    const store = new CodeReviewActivityStore();
    const key = CodeReviewActivityStore.key('C:/projects/foo', 'job-1');

    store.markRunning(key);
    store.markRunning(key);
    store.clear(key);

    expect(store.isRunning(key)).toBe(false);
    // Clearing an absent key is a no-op, not an error.
    expect(() => store.clear(key)).not.toThrow();
  });
});
