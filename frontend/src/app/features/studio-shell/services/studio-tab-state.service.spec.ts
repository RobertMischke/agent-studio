import { TestBed } from '@angular/core/testing';
import { StudioTabStateService } from './studio-tab-state.service';
import type { StudioTab } from '../studio-shell.types';
import { studioTabKey } from '../studio-shell.types';

const STORAGE_KEY = 'atp.studio.tabs.v1';
const STICKY_KEY = 'board:__all__';

describe('StudioTabStateService', () => {
  let svc: StudioTabStateService;

  beforeEach(() => {
    localStorage.removeItem(STORAGE_KEY);
    TestBed.configureTestingModule({ providers: [StudioTabStateService] });
    svc = TestBed.inject(StudioTabStateService);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem(STORAGE_KEY);
  });

  describe('sticky default board tab', () => {
    it('mounts the sticky board:__all__ tab at boot when storage is empty', () => {
      expect(svc.tabs()).toHaveLength(1);
      const sticky = svc.tabs()[0];
      expect(studioTabKey(sticky)).toBe(STICKY_KEY);
      expect(sticky.kind).toBe('board');
      expect((sticky as { sticky?: boolean }).sticky).toBe(true);
      expect(svc.activeKey()).toBe(STICKY_KEY);
      expect(svc.stickyKey()).toBe(STICKY_KEY);
      expect(svc.isStickyKey(STICKY_KEY)).toBe(true);
    });

    it('promotes an existing board:__all__ tab from a pre-sticky snapshot', () => {
      // Mimic an old persisted state from before the sticky feature shipped.
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({
          v: 1,
          tabs: [
            { kind: 'board', projectName: '__all__' },
            { kind: 'task', taskKey: 'a' },
          ],
          activeKey: 'task:a',
        }),
      );
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);
      expect(restored.tabs()).toHaveLength(2);
      expect(restored.isStickyKey(STICKY_KEY)).toBe(true);
      // Active key should NOT be overridden when a valid one was persisted.
      expect(restored.activeKey()).toBe('task:a');
    });

    it('activateSticky() focuses the sticky tab from any other state', () => {
      svc.open({ kind: 'task', taskKey: 'a' });
      expect(svc.activeKey()).toBe('task:a');
      const key = svc.activateSticky();
      expect(key).toBe(STICKY_KEY);
      expect(svc.activeKey()).toBe(STICKY_KEY);
    });
  });

  it('opens a tab and makes it active', () => {
    const tab: StudioTab = { kind: 'board', projectName: 'demo' };
    svc.open(tab);
    expect(svc.tabs()).toContainEqual(tab);
    expect(svc.activeKey()).toBe('board:demo');
    expect(svc.activeTab()).toEqual(tab);
  });

  it('focuses an already-open tab instead of duplicating', () => {
    const tab: StudioTab = { kind: 'task', taskKey: 'demo|x' };
    svc.open(tab);
    svc.open(tab);
    // Sticky + 1 task = 2.
    expect(svc.tabs()).toHaveLength(2);
    expect(svc.activeKey()).toBe('task:demo|x');
  });

  it('closing the active tab falls back to the previous one', () => {
    svc.open({ kind: 'task', taskKey: 'a' });
    svc.open({ kind: 'task', taskKey: 'b' });
    expect(svc.activeKey()).toBe('task:b');
    svc.close('task:b');
    expect(svc.tabs()).toHaveLength(2); // sticky + task:a
    expect(svc.activeKey()).toBe('task:a');
  });

  it('closing every non-sticky tab leaves the sticky board active', () => {
    const tab: StudioTab = { kind: 'task', taskKey: 'only' };
    svc.open(tab);
    svc.close(studioTabKey(tab));
    expect(svc.tabs().map(t => studioTabKey(t))).toEqual([STICKY_KEY]);
    expect(svc.activeKey()).toBe(STICKY_KEY);
    expect(svc.activeTab()).not.toBeNull();
  });

  it('close() on the sticky key is a no-op', () => {
    svc.open({ kind: 'task', taskKey: 'a' });
    svc.close(STICKY_KEY);
    expect(svc.tabs().map(t => studioTabKey(t))).toEqual([STICKY_KEY, 'task:a']);
  });

  it('closeOthers keeps the named tab AND the sticky tab', () => {
    svc.open({ kind: 'board', projectName: 'demo' });
    svc.open({ kind: 'task', taskKey: 'a' });
    svc.open({ kind: 'task', taskKey: 'b' });
    svc.closeOthers('task:a');
    expect(svc.tabs().map(t => studioTabKey(t)).sort())
      .toEqual([STICKY_KEY, 'task:a'].sort());
    expect(svc.activeKey()).toBe('task:a');
  });

  it('closeOthers on the sticky tab keeps only the sticky tab', () => {
    svc.open({ kind: 'task', taskKey: 'a' });
    svc.open({ kind: 'task', taskKey: 'b' });
    svc.closeOthers(STICKY_KEY);
    expect(svc.tabs().map(t => studioTabKey(t))).toEqual([STICKY_KEY]);
    expect(svc.activeKey()).toBe(STICKY_KEY);
  });

  it('closeRight removes everything strictly after the anchor but keeps the sticky tab', () => {
    // Sticky sits at index 0, then board:demo, task:a, task:b.
    svc.open({ kind: 'board', projectName: 'demo' });
    svc.open({ kind: 'task', taskKey: 'a' });
    svc.open({ kind: 'task', taskKey: 'b' });
    svc.closeRight('task:a');
    expect(svc.tabs().map(t => studioTabKey(t)))
      .toEqual([STICKY_KEY, 'board:demo', 'task:a']);
  });

  it('closeLeft removes everything strictly before the anchor but keeps the sticky tab', () => {
    svc.open({ kind: 'board', projectName: 'demo' });
    svc.open({ kind: 'task', taskKey: 'a' });
    svc.open({ kind: 'task', taskKey: 'b' });
    svc.closeLeft('task:a');
    expect(svc.tabs().map(t => studioTabKey(t)).sort())
      .toEqual([STICKY_KEY, 'task:a', 'task:b'].sort());
  });

  it('closeAll preserves the sticky tab and activates it', () => {
    svc.open({ kind: 'board', projectName: 'demo' });
    svc.open({ kind: 'task', taskKey: 'a' });
    svc.closeAll();
    expect(svc.tabs().map(t => studioTabKey(t))).toEqual([STICKY_KEY]);
    expect(svc.activeKey()).toBe(STICKY_KEY);
  });

  describe('move (drag-reorder)', () => {
    function seed(): void {
      svc.open({ kind: 'board', projectName: 'demo' });
      svc.open({ kind: 'task', taskKey: 'a' });
      svc.open({ kind: 'task', taskKey: 'b' });
      svc.open({ kind: 'task', taskKey: 'c' });
      // Layout: [sticky, board:demo, task:a, task:b, task:c]. Active = task:c.
    }

    it('moves a tab to land before the target', () => {
      seed();
      svc.move('task:c', 'task:a');
      expect(svc.tabs().map(t => studioTabKey(t)))
        .toEqual([STICKY_KEY, 'board:demo', 'task:c', 'task:a', 'task:b']);
    });

    it('moves a tab to the end when target is null', () => {
      seed();
      svc.move('board:demo', null);
      expect(svc.tabs().map(t => studioTabKey(t)))
        .toEqual([STICKY_KEY, 'task:a', 'task:b', 'task:c', 'board:demo']);
    });

    it('keeps the active key stable across a move', () => {
      seed();
      expect(svc.activeKey()).toBe('task:c');
      svc.move('task:a', 'task:c');
      expect(svc.activeKey()).toBe('task:c');
    });

    it('is a no-op when source and target match', () => {
      seed();
      const before = svc.tabs().map(t => studioTabKey(t));
      svc.move('task:b', 'task:b');
      expect(svc.tabs().map(t => studioTabKey(t))).toEqual(before);
    });

    it('is a no-op when the source key does not exist', () => {
      seed();
      const before = svc.tabs().map(t => studioTabKey(t));
      svc.move('task:does-not-exist', 'task:a');
      expect(svc.tabs().map(t => studioTabKey(t))).toEqual(before);
    });

    it('handles dropping a tab right after itself (forward move by one)', () => {
      seed();
      svc.move('task:a', 'task:c');
      expect(svc.tabs().map(t => studioTabKey(t)))
        .toEqual([STICKY_KEY, 'board:demo', 'task:b', 'task:a', 'task:c']);
    });
  });

  describe('persistence across reloads', () => {
    it('writes the current state to localStorage on every change', () => {
      svc.open({ kind: 'task', taskKey: 'a' });
      const raw = localStorage.getItem(STORAGE_KEY);
      expect(raw).not.toBeNull();
      const parsed = JSON.parse(raw!);
      expect(parsed.v).toBe(1);
      // Sticky + task:a = 2.
      expect(parsed.tabs).toHaveLength(2);
      expect(parsed.activeKey).toBe('task:a');
    });

    it('restores tabs + active key when constructed against a previously-written payload', () => {
      svc.open({ kind: 'board', projectName: 'demo' });
      svc.open({ kind: 'task', taskKey: 'a' });
      svc.open({ kind: 'task', taskKey: 'b' });
      // Drop the existing instance and spin up a fresh one against
      // the same localStorage — mimics a page reload.
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);
      expect(restored.tabs().map(t => studioTabKey(t)))
        .toEqual([STICKY_KEY, 'board:demo', 'task:a', 'task:b']);
      expect(restored.activeKey()).toBe('task:b');
      expect(restored.isStickyKey(STICKY_KEY)).toBe(true);
    });

    it('drops the payload when the version prefix does not match but still mounts the sticky tab', () => {
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({ v: 99, tabs: [{ kind: 'task', taskKey: 'z' }], activeKey: 'task:z' }),
      );
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);
      expect(restored.tabs().map(t => studioTabKey(t))).toEqual([STICKY_KEY]);
      expect(restored.activeKey()).toBe(STICKY_KEY);
    });

    it('falls back to the trailing tab when the persisted activeKey is gone', () => {
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({
          v: 1,
          tabs: [
            { kind: 'board', projectName: '__all__', sticky: true },
            { kind: 'board', projectName: 'demo' },
            { kind: 'task', taskKey: 'a' },
          ],
          activeKey: 'task:missing',
        }),
      );
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);
      expect(restored.tabs()).toHaveLength(3);
      expect(restored.activeKey()).toBe('task:a');
    });

    it('survives a corrupt payload without throwing and still mounts the sticky tab', () => {
      localStorage.setItem(STORAGE_KEY, 'not-json{');
      expect(() => {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: [StudioTabStateService] });
        TestBed.inject(StudioTabStateService);
      }).not.toThrow();
      const restored = TestBed.inject(StudioTabStateService);
      expect(restored.tabs().map(t => studioTabKey(t))).toEqual([STICKY_KEY]);
    });
  });
});
