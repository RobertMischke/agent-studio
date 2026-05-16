import { TestBed } from '@angular/core/testing';
import { StudioTabStateService } from './studio-tab-state.service';
import type { StudioTab } from '../studio-shell.types';
import { studioTabKey } from '../studio-shell.types';

describe('StudioTabStateService', () => {
  let svc: StudioTabStateService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [StudioTabStateService] });
    svc = TestBed.inject(StudioTabStateService);
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
});
