import { TestBed } from '@angular/core/testing';
import { StudioTabStateService } from './studio-tab-state.service';
import type { StudioTab } from '../studio-shell.types';
import { studioTabKey } from '../studio-shell.types';

const STORAGE_KEY = 'atp.studio.tabs.v1';

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

  it('opens a tab and makes it active', () => {
    const tab: StudioTab = { kind: 'board', projectName: 'demo' };
    svc.open(tab);
    expect(svc.tabs()).toEqual([tab]);
    expect(svc.activeKey()).toBe('board:demo');
    expect(svc.activeTab()).toEqual(tab);
  });

  it('focuses an already-open tab instead of duplicating', () => {
    const tab: StudioTab = { kind: 'task', jobKey: 'demo|x' };
    svc.open(tab);
    svc.open(tab);
    expect(svc.tabs()).toHaveLength(1);
    expect(svc.activeKey()).toBe('task:demo|x');
  });

  it('closing the active tab falls back to the previous one', () => {
    svc.open({ kind: 'board', projectName: 'demo' });
    svc.open({ kind: 'task', jobKey: 'a' });
    svc.open({ kind: 'task', jobKey: 'b' });
    expect(svc.activeKey()).toBe('task:b');
    svc.close('task:b');
    expect(svc.tabs()).toHaveLength(2);
    expect(svc.activeKey()).toBe('task:a');
  });

  it('closing the last tab resets to no active tab', () => {
    const tab: StudioTab = { kind: 'task', jobKey: 'only' };
    svc.open(tab);
    svc.close(studioTabKey(tab));
    expect(svc.tabs()).toEqual([]);
    expect(svc.activeKey()).toBeNull();
    expect(svc.activeTab()).toBeNull();
  });

  it('closeOthers keeps only the named tab', () => {
    svc.open({ kind: 'board', projectName: 'demo' });
    svc.open({ kind: 'task', jobKey: 'a' });
    svc.open({ kind: 'task', jobKey: 'b' });
    svc.closeOthers('task:a');
    expect(svc.tabs().map(t => studioTabKey(t))).toEqual(['task:a']);
    expect(svc.activeKey()).toBe('task:a');
  });

  it('closeRight removes everything strictly after the anchor', () => {
    svc.open({ kind: 'board', projectName: 'demo' });
    svc.open({ kind: 'task', jobKey: 'a' });
    svc.open({ kind: 'task', jobKey: 'b' });
    svc.closeRight('task:a');
    expect(svc.tabs().map(t => studioTabKey(t))).toEqual(['board:demo', 'task:a']);
  });

  it('closeLeft removes everything strictly before the anchor', () => {
    svc.open({ kind: 'board', projectName: 'demo' });
    svc.open({ kind: 'task', jobKey: 'a' });
    svc.open({ kind: 'task', jobKey: 'b' });
    svc.closeLeft('task:a');
    expect(svc.tabs().map(t => studioTabKey(t))).toEqual(['task:a', 'task:b']);
  });

  it('closeAll wipes everything', () => {
    svc.open({ kind: 'board', projectName: 'demo' });
    svc.open({ kind: 'task', jobKey: 'a' });
    svc.closeAll();
    expect(svc.tabs()).toEqual([]);
    expect(svc.activeKey()).toBeNull();
  });

  describe('move (drag-reorder)', () => {
    function seed(): void {
      svc.open({ kind: 'board', projectName: 'demo' });
      svc.open({ kind: 'task', jobKey: 'a' });
      svc.open({ kind: 'task', jobKey: 'b' });
      svc.open({ kind: 'task', jobKey: 'c' });
      // Active = task:c after seeding.
    }

    it('moves a tab to land before the target', () => {
      seed();
      svc.move('task:c', 'task:a');
      expect(svc.tabs().map(t => studioTabKey(t))).toEqual(['board:demo', 'task:c', 'task:a', 'task:b']);
    });

    it('moves a tab to the end when target is null', () => {
      seed();
      svc.move('board:demo', null);
      expect(svc.tabs().map(t => studioTabKey(t))).toEqual(['task:a', 'task:b', 'task:c', 'board:demo']);
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
      expect(svc.tabs().map(t => studioTabKey(t))).toEqual(['board:demo', 'task:b', 'task:a', 'task:c']);
    });
  });

  describe('persistence across reloads', () => {
    it('writes the current state to localStorage on every change', () => {
      svc.open({ kind: 'board', projectName: 'demo' });
      svc.open({ kind: 'task', jobKey: 'a' });
      const raw = localStorage.getItem(STORAGE_KEY);
      expect(raw).not.toBeNull();
      const parsed = JSON.parse(raw!);
      expect(parsed.v).toBe(1);
      expect(parsed.tabs).toHaveLength(2);
      expect(parsed.activeKey).toBe('task:a');
    });

    it('restores tabs + active key when constructed against a previously-written payload', () => {
      svc.open({ kind: 'board', projectName: 'demo' });
      svc.open({ kind: 'task', jobKey: 'a' });
      svc.open({ kind: 'task', jobKey: 'b' });
      // Drop the existing instance and spin up a fresh one against
      // the same localStorage — mimics a page reload.
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);
      expect(restored.tabs().map(t => studioTabKey(t))).toEqual(['board:demo', 'task:a', 'task:b']);
      expect(restored.activeKey()).toBe('task:b');
    });

    it('drops the payload when the version prefix does not match', () => {
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({ v: 99, tabs: [{ kind: 'task', jobKey: 'z' }], activeKey: 'task:z' }),
      );
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);
      expect(restored.tabs()).toEqual([]);
      expect(restored.activeKey()).toBeNull();
    });

    it('falls back to the trailing tab when the persisted activeKey is gone', () => {
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({
          v: 1,
          tabs: [
            { kind: 'board', projectName: 'demo' },
            { kind: 'task', jobKey: 'a' },
          ],
          activeKey: 'task:missing',
        }),
      );
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [StudioTabStateService] });
      const restored = TestBed.inject(StudioTabStateService);
      expect(restored.tabs()).toHaveLength(2);
      expect(restored.activeKey()).toBe('task:a');
    });

    it('survives a corrupt payload without throwing', () => {
      localStorage.setItem(STORAGE_KEY, 'not-json{');
      expect(() => {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: [StudioTabStateService] });
        TestBed.inject(StudioTabStateService);
      }).not.toThrow();
      const restored = TestBed.inject(StudioTabStateService);
      expect(restored.tabs()).toEqual([]);
    });
  });
});
