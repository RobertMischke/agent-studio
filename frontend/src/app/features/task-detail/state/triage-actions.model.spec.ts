import { describe, expect, it } from 'vitest';
import { overflowActionsFor, primaryActionFor } from './triage-actions.model';

function overflowIds(state: string): string[] {
  return overflowActionsFor(state).map(b => b.id);
}

describe('primaryActionFor — Enter-bound primary per source lane', () => {
  it('labels the Completed lane primary "Archive & Next" and moves to 7-archive', () => {
    const primary = primaryActionFor('6-completed');
    expect(primary).not.toBeNull();
    expect(primary!.id).toBe('archive');
    expect(primary!.label).toBe('Archive & Next');
    expect(primary!.intent).toEqual({ kind: 'move', targetState: '7-archive' });
  });

  it('leaves the Review lane primary unchanged ("Send to Complete" → 6-completed)', () => {
    const primary = primaryActionFor('5-human-review');
    expect(primary).not.toBeNull();
    expect(primary!.id).toBe('mark-done');
    expect(primary!.label).toBe('Send to Complete');
    expect(primary!.intent).toEqual({ kind: 'move', targetState: '6-completed' });
  });

  it('leaves the Post Processing lane without a primary (Enter is a no-op)', () => {
    expect(primaryActionFor('4-auto-review')).toBeNull();
  });
});

describe('overflowActionsFor — Move to Completed / Move to Archive', () => {
  it('offers both moves from Ready, next to Send to Backlog and before Edit/Delete', () => {
    const ids = overflowIds('2-ready');
    expect(ids).toEqual([
      'move-to-top',
      'send-to-backlog',
      'move-to-completed',
      'move-to-archive',
      'edit-prompt',
      'delete',
    ]);
  });

  it('offers both moves from Backlog', () => {
    const ids = overflowIds('0-backlog');
    expect(ids).toContain('move-to-completed');
    expect(ids).toContain('move-to-archive');
    // Move entries precede the Edit/Delete safety nets.
    expect(ids.indexOf('move-to-completed')).toBeLessThan(ids.indexOf('edit-prompt'));
    expect(ids.indexOf('move-to-archive')).toBeLessThan(ids.indexOf('delete'));
  });

  it('offers both moves from a generic lane that has neither target (preparation)', () => {
    const ids = overflowIds('1-preparation');
    expect(ids).toContain('move-to-completed');
    expect(ids).toContain('move-to-archive');
  });

  it('skips Move to Completed when the lane already routes to 6-completed', () => {
    // 5-human-review's primary "Send to Complete" targets 6-completed, so the
    // overflow must not add a duplicate. Move to Archive is still offered.
    const ids = overflowIds('5-human-review');
    expect(ids).not.toContain('move-to-completed');
    expect(ids).toContain('move-to-archive');
  });

  it('skips Move to Archive when the lane already routes to 7-archive', () => {
    // 6-completed's primary "Archive" targets 7-archive. Move to Completed is
    // suppressed too because 6-completed is the current lane.
    const ids = overflowIds('6-completed');
    expect(ids).not.toContain('move-to-archive');
    expect(ids).not.toContain('move-to-completed');
  });

  it('suppresses the current-lane target (no self-move from Archive)', () => {
    const ids = overflowIds('7-archive');
    expect(ids).not.toContain('move-to-archive');
    // Moving an archived card forward to Completed is still allowed.
    expect(ids).toContain('move-to-completed');
  });
});
