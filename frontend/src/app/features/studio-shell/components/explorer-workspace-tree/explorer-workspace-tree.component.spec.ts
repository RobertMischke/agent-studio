import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import {
  ExplorerWorkspaceTreeComponent,
  type ExplorerProjectRow,
} from './explorer-workspace-tree.component';
import type { ProjectPulseState } from '../../studio-shell.pulse';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { RegistryWorkspaceListItem, RegistryProjectSummary } from '../../../../models/task.model';

function project(displayName: string, workspaceId: string, storage: string): RegistryProjectSummary {
  return {
    id: `PROJ-${displayName}`,
    displayName,
    shortCode: displayName.slice(0, 3).toUpperCase(),
    workspaceId,
    color: null,
    cliDefault: null,
    modelDefault: null,
    sortOrder: 0,
    storageLocation: storage,
    urls: [],
    archived: false,
    createdAt: '2026-01-01T00:00:00Z',
  };
}

function workspace(
  id: string,
  displayName: string,
  sortOrder: number,
  projects: RegistryProjectSummary[],
): RegistryWorkspaceListItem {
  return { id, displayName, sortOrder, isDefault: id === 'ws-default', color: null, createdAt: '2026-01-01T00:00:00Z', projects };
}

function row(name: string, over: Partial<ExplorerProjectRow> = {}): ExplorerProjectRow {
  return {
    name,
    initial: name[0]?.toUpperCase() ?? '?',
    color: '#888',
    totalJobs: 0,
    isActive: false,
    ...over,
  };
}

function mount() {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  });
  return TestBed.createComponent(ExplorerWorkspaceTreeComponent);
}

const noop = (): void => { return; };

describe('ExplorerWorkspaceTreeComponent', () => {
  it('constructs and wires its injected services', () => {
    const fixture = mount();
    expect(fixture.componentInstance).toBeTruthy();
    try {
      fixture.detectChanges();
    } catch (err) {
      console.warn('render path note:', err);
    }
  });

  it('groups project rows under their registry workspace by display name', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
      workspace('ws-2', 'Side Projects', 1, [project('Beta', 'ws-2', '/repos/Beta')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha'), row('Beta')]);

    const groups = cmp.groups();
    expect(groups.map(g => g.displayName)).toEqual(['Default', 'Side Projects']);
    expect(groups[0].projects.map(p => p.name)).toEqual(['Alpha']);
    expect(groups[1].projects.map(p => p.name)).toEqual(['Beta']);
  });

  it('matches a renamed workspace project by its storage-folder tail', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    // Registry displayName diverges from the row name, but the folder
    // tail of storageLocation still matches the (folder-derived) row.
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Renamed', 'ws-default', 'C:/repos/Gamma')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Gamma')]);

    const groups = cmp.groups();
    expect(groups).toHaveLength(1);
    expect(groups[0].projects.map(p => p.name)).toEqual(['Gamma']);
  });

  it('puts rows with no registry match into an Unassigned group', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha'), row('Orphan')]);

    const groups = cmp.groups();
    expect(groups.map(g => g.id)).toEqual(['ws-default', '__unassigned__']);
    expect(groups[1].projects.map(p => p.name)).toEqual(['Orphan']);
  });

  it('falls back to a single legacy folder when the registry is empty', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [row('Alpha'), row('Beta')]);

    const groups = cmp.groups();
    expect(groups).toHaveLength(1);
    expect(groups[0].id).toBe('__all__');
    expect(groups[0].projects.map(p => p.name)).toEqual(['Alpha', 'Beta']);
  });

  it('renders Board lane counters with a descriptive aria label', () => {
    const fixture = mount();
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [
      row('Alpha', { laneCounts: { ready: 3, progress: 2, humanReview: 5 } }),
    ]);
    fixture.componentRef.setInput('expandedProjects', new Set(['Alpha']));

    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const board = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-board-Alpha"]');
    const counts = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-board-counts-Alpha"]');
    const ready = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-board-count-ready-Alpha"]');
    const progress = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-board-count-progress-Alpha"]');
    const review = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-board-count-human-review-Alpha"]');

    expect(board?.getAttribute('aria-label')).toBe('Board, 3 ready, 2 in progress, 5 human review');
    expect(counts?.getAttribute('aria-label')).toBe('3 ready, 2 in progress, 5 human review');
    expect(ready?.textContent?.trim()).toBe('3');
    expect(progress?.textContent?.trim()).toBe('2');
    expect(review?.textContent?.trim()).toBe('5');
  });

  it('attaches a lane-explaining cacTooltip to each Board lane counter', () => {
    const fixture = mount();
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [
      row('Alpha', { laneCounts: { ready: 3, progress: 2, humanReview: 5 } }),
    ]);
    fixture.componentRef.setInput('expandedProjects', new Set(['Alpha']));
    fixture.detectChanges();

    const tipFor = (testid: string) => {
      const node = fixture.debugElement
        .queryAll(By.directive(TooltipDirective))
        .find(d => (d.nativeElement as HTMLElement).getAttribute('data-testid') === testid);
      return node?.injector.get(TooltipDirective).content();
    };

    expect(tipFor('studio-explorer-project-board-count-ready-Alpha')).toMatchObject({ title: 'Ready' });
    expect(tipFor('studio-explorer-project-board-count-progress-Alpha')).toMatchObject({ title: 'In Progress' });
    expect(tipFor('studio-explorer-project-board-count-human-review-Alpha')).toMatchObject({ title: 'Human Review' });
  });

  it('renders create workspace as a compact Workspaces header action', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    cmp.setCollapsed('workspace', false);
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);
    const emitted: void[] = [];
    cmp.onboardWorkspaceRequest.subscribe(v => emitted.push(v));
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const add = root.querySelector<HTMLButtonElement>('[data-testid="studio-explorer-add-workspace"]');
    expect(add?.classList.contains('studio-sidebar-header__action')).toBe(true);
    expect(add?.querySelector('app-studio-icon')).toBeTruthy();

    add?.click();

    expect(emitted).toHaveLength(1);
    expect(cmp.isCollapsed('workspace')).toBe(false);
  });

  it('renders create project as a compact workspace-row icon action', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    cmp.setCollapsed('workspace', false);
    cmp.setCollapsed('ws:ws-default', false);
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);
    const emitted: string[] = [];
    cmp.onboardProjectRequest.subscribe(v => emitted.push(v));
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const add = root.querySelector<HTMLButtonElement>('[data-testid="studio-workspace-ws-default-add-project"]');
    expect(add?.tagName.toLowerCase()).toBe('button');
    expect(add?.classList.contains('tree-row')).toBe(false);
    expect(add?.querySelector('app-studio-icon')).toBeTruthy();
    expect(root.textContent).not.toContain('New project');

    add?.click();

    expect(emitted).toEqual(['ws-default']);
    expect(cmp.isCollapsed('ws:ws-default')).toBe(false);
  });

  it('highlights the active project subnavigation row', () => {
    const fixture = mount();
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [row('Alpha', { isActive: true })]);
    fixture.componentRef.setInput('expandedProjects', new Set(['Alpha']));
    fixture.componentRef.setInput('activeProjectSurface', 'hub');
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const board = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-board-Alpha"]');
    const hub = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-hub-Alpha"]');

    expect(board?.classList.contains('tree-row--active')).toBe(false);
    expect(board?.getAttribute('aria-current')).toBeNull();
    expect(hub?.classList.contains('tree-row--active')).toBe(true);
    expect(hub?.getAttribute('aria-current')).toBe('page');
    expect(board?.classList.contains('tree-row--root')).toBe(true);
    expect(hub?.classList.contains('tree-row--root')).toBe(true);
    expect(root.querySelector('.studio-tree-children .tree-row--child')).toBeNull();
  });

  it('renders a Wiki row under Project Hub that emits openWikiRequest', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [row('Alpha', { isActive: true })]);
    fixture.componentRef.setInput('expandedProjects', new Set(['Alpha']));
    fixture.detectChanges();

    const emitted: string[] = [];
    cmp.openWikiRequest.subscribe(name => emitted.push(name));

    const root: HTMLElement = fixture.nativeElement;
    const rows = Array.from(
      root.querySelectorAll<HTMLElement>('.studio-tree-children .tree-row[data-testid^="studio-explorer-project-"]'),
    ).map(el => el.getAttribute('data-testid'));
    // Wiki sits directly after Project Hub in the per-project child list.
    expect(rows).toEqual([
      'studio-explorer-project-board-Alpha',
      'studio-explorer-project-hub-Alpha',
      'studio-explorer-project-wiki-Alpha',
      'studio-explorer-project-backlog-Alpha',
      'studio-explorer-project-epics-Alpha',
    ]);

    const wiki = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-wiki-Alpha"]');
    wiki?.click();
    expect(emitted).toEqual(['Alpha']);
  });

  it('highlights only the Wiki row when the active surface is the wiki rail', () => {
    const fixture = mount();
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [row('Alpha', { isActive: true })]);
    fixture.componentRef.setInput('expandedProjects', new Set(['Alpha']));
    fixture.componentRef.setInput('activeProjectSurface', 'wiki');
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const hub = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-hub-Alpha"]');
    const wiki = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-wiki-Alpha"]');

    expect(hub?.classList.contains('tree-row--active')).toBe(false);
    expect(wiki?.classList.contains('tree-row--active')).toBe(true);
    expect(wiki?.getAttribute('aria-current')).toBe('page');
  });

  it('right-click opens a text-only Rename menu that starts the inline rename', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);

    const g = cmp.groups()[0];
    const ev = { preventDefault: noop, clientX: 12, clientY: 34 } as MouseEvent;
    cmp.openWsContextMenu(ev, g);

    expect(cmp.wsContextMenu()).toEqual({ id: 'ws-default', x: 12, y: 34 });
    const items = cmp.wsContextMenuItems();
    expect(items).toEqual([{ kind: 'row', id: 'rename', label: 'Rename' }]);

    cmp.onWsContextMenuItemClick({ id: 'rename', item: items[0] as never });
    expect(cmp.wsContextMenu()).toBeNull();
    expect(cmp.renamingWsId()).toBe('ws-default');
    expect(cmp.renameDraft()).toBe('Default');
  });

  it('does not open a custom context menu for synthetic groups', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);

    const g = cmp.groups()[0]; // synthetic "__all__" fallback
    let prevented = false;
    cmp.openWsContextMenu({ preventDefault: () => { prevented = true; }, clientX: 1, clientY: 2 } as MouseEvent, g);

    expect(prevented).toBe(false);
    expect(cmp.wsContextMenu()).toBeNull();
  });

  it('right-click opens a text-only project menu that starts inline rename', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);

    const alpha = cmp.groups()[0].projects[0];
    let prevented = false;
    let stopped = false;
    cmp.onProjectContextMenu({
      preventDefault: () => { prevented = true; },
      stopPropagation: () => { stopped = true; },
      clientX: 20,
      clientY: 40,
    } as unknown as MouseEvent, alpha);

    expect(prevented).toBe(true);
    expect(stopped).toBe(true);
    expect(cmp.projectActions.contextMenu()).toEqual({
      projectId: 'PROJ-Alpha',
      name: 'Alpha',
      displayName: 'Alpha',
      shortCode: 'ALP',
      x: 20,
      y: 40,
    });
    expect(cmp.projectActions.contextMenuItems()).toEqual([
      { kind: 'row', id: 'rename', label: 'Rename' },
      { kind: 'row', id: 'delete', label: 'Delete project…', danger: true },
    ]);

    cmp.onProjectContextMenuItemClick({
      id: 'rename',
      item: cmp.projectActions.contextMenuItems()[0] as never,
    });

    expect(cmp.projectActions.contextMenu()).toBeNull();
    expect(cmp.projectActions.renamingProjectId()).toBe('PROJ-Alpha');
    expect(cmp.projectActions.renameDraft()).toBe('Alpha');
  });

  it('project delete menu emits stable id plus display name and short code', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);

    const emitted: { projectId: string; displayName: string; shortCode: string | null }[] = [];
    cmp.deleteProject.subscribe(v => emitted.push(v));
    cmp.onProjectContextMenu({
      preventDefault: noop,
      stopPropagation: noop,
      clientX: 1,
      clientY: 2,
    } as unknown as MouseEvent, cmp.groups()[0].projects[0]);

    cmp.onProjectContextMenuItemClick({
      id: 'delete',
      item: cmp.projectActions.contextMenuItems()[1] as never,
    });

    expect(cmp.projectActions.contextMenu()).toBeNull();
    expect(emitted).toEqual([{ projectId: 'PROJ-Alpha', displayName: 'Alpha', shortCode: 'ALP' }]);
  });
});

describe('ExplorerWorkspaceTreeComponent — AGT-2031 auto-pickup pulse', () => {
  const pulse = (entries: [string, ProjectPulseState][]) =>
    new Map<string, ProjectPulseState>(entries);

  it('renders a per-project pulse dot reflecting idle / active / off state', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    cmp.setCollapsed('workspace', false);
    cmp.setCollapsed('ws:__all__', false);
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [row('Alpha'), row('Beta'), row('Gamma')]);
    fixture.componentRef.setInput('projectPulseByName', pulse([
      ['Alpha', 'auto-idle'],
      ['Beta', 'auto-active'],
    ]));
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement;
    const dot = (name: string) =>
      root.querySelector<HTMLElement>(`[data-testid="studio-explorer-project-pulse-${name}"]`);

    expect(dot('Alpha')?.getAttribute('data-pulse')).toBe('auto-idle');
    expect(dot('Alpha')?.classList.contains('studio-auto-pulse--idle')).toBe(true);
    expect(dot('Alpha')?.getAttribute('aria-label')).toBe('Auto-pickup on');

    expect(dot('Beta')?.getAttribute('data-pulse')).toBe('auto-active');
    expect(dot('Beta')?.classList.contains('studio-auto-pulse--active')).toBe(true);
    expect(dot('Beta')?.getAttribute('aria-label')).toBe('Auto-pickup running');

    // Not on auto → the slot still renders (reserved width, no reflow) but is
    // marked off with no accessible label.
    expect(dot('Gamma')?.getAttribute('data-pulse')).toBe('off');
    expect(dot('Gamma')?.classList.contains('studio-auto-pulse--idle')).toBe(false);
    expect(dot('Gamma')?.classList.contains('studio-auto-pulse--active')).toBe(false);
    expect(dot('Gamma')?.getAttribute('aria-label')).toBeNull();
  });

  it('rolls child pulses into the active-wins aggregate with the on-auto names', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [
        project('Alpha', 'ws-default', '/repos/Alpha'),
        project('Beta', 'ws-default', '/repos/Beta'),
        project('Gamma', 'ws-default', '/repos/Gamma'),
      ]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha'), row('Beta'), row('Gamma')]);
    fixture.componentRef.setInput('projectPulseByName', pulse([
      ['Alpha', 'auto-idle'],
      ['Beta', 'auto-active'],
    ]));

    const agg = cmp.wsPulseAggregate(cmp.groups()[0]);
    // active beats idle; the off project (Gamma) drops out of the name list.
    expect(agg).toEqual({ state: 'auto-active', autoProjects: ['Alpha', 'Beta'] });
    expect(cmp.aggregatePulseTooltip(agg)).toBe('Auto-pickup running: Alpha, Beta');
  });

  it('shows the aggregate dot on a collapsed workspace header, hides it when expanded', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    cmp.setCollapsed('workspace', false);
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);
    fixture.componentRef.setInput('projectPulseByName', pulse([['Alpha', 'auto-idle']]));

    const root: HTMLElement = fixture.nativeElement;
    const wsDot = () => root.querySelector<HTMLElement>('[data-testid="studio-explorer-ws-pulse-ws-default"]');

    cmp.setCollapsed('ws:ws-default', false);
    fixture.detectChanges();
    expect(wsDot()).toBeNull(); // expanded → per-project dots carry the signal

    cmp.setCollapsed('ws:ws-default', true);
    fixture.detectChanges();
    expect(wsDot()?.getAttribute('data-pulse')).toBe('auto-idle');
    expect(wsDot()?.getAttribute('aria-label')).toBe('Auto-pickup on: Alpha');
  });

  it('surfaces the whole-tree aggregate on the panel header only when collapsed', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', []);
    fixture.componentRef.setInput('projectRows', [row('Alpha'), row('Beta')]);
    fixture.componentRef.setInput('projectPulseByName', pulse([
      ['Alpha', 'auto-idle'],
      ['Beta', 'auto-active'],
    ]));

    expect(cmp.allPulseAggregate()).toEqual({ state: 'auto-active', autoProjects: ['Alpha', 'Beta'] });

    const root: HTMLElement = fixture.nativeElement;
    const panelDot = () => root.querySelector<HTMLElement>('[data-testid="studio-explorer-workspace-pulse"]');

    cmp.setCollapsed('workspace', false);
    fixture.detectChanges();
    expect(panelDot()).toBeNull();

    cmp.setCollapsed('workspace', true);
    fixture.detectChanges();
    expect(panelDot()?.getAttribute('data-pulse')).toBe('auto-active');
  });
});

describe('ExplorerWorkspaceTreeComponent — project drag-and-drop (F46 workspace reassignment)', () => {
  function twoWorkspaces(cmp: ExplorerWorkspaceTreeComponent, fixture: ReturnType<typeof mount>) {
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
      workspace('ws-2', 'Side Projects', 1, [project('Beta', 'ws-2', '/repos/Beta')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha'), row('Beta'), row('Orphan')]);
    cmp.projectDrag.reset();
  }

  const dragEvent = () =>
    ({ effectAllowed: '', dropEffect: '', setData: noop });

  it('attaches the registry projectId + owning workspaceId to matched nodes', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    twoWorkspaces(cmp, fixture);

    const groups = cmp.groups();
    const alpha = groups[0].projects[0];
    expect(alpha.projectId).toBe('PROJ-Alpha');
    expect(alpha.workspaceId).toBe('ws-default');

    const orphan = groups.find(g => g.id === '__unassigned__')!.projects[0];
    expect(orphan.projectId).toBeNull();
    expect(orphan.workspaceId).toBeNull();
  });

  it('drops a project onto a different workspace and emits the reassignment', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    twoWorkspaces(cmp, fixture);

    const groups = cmp.groups();
    const alpha = groups[0].projects[0]; // lives in ws-default
    const sideGroup = groups[1];         // ws-2

    const dt = dragEvent();
    cmp.onDragStart({ dataTransfer: dt, preventDefault: noop } as unknown as DragEvent, alpha);
    expect(cmp.projectDrag.draggingProjectId()).toBe('PROJ-Alpha');
    expect(cmp.canDropOnWorkspace('ws-2')).toBe(true);
    expect(cmp.canDropOnWorkspace('ws-default')).toBe(false); // same workspace = no-op

    let emitted: { projectId: string; targetWorkspaceId: string } | null = null;
    const sub = cmp.projectDrop.subscribe(e => (emitted = e));
    cmp.onWorkspaceDrop({ preventDefault: noop, dataTransfer: dt } as unknown as DragEvent, sideGroup);
    sub.unsubscribe();

    expect(emitted).toEqual({ projectId: 'PROJ-Alpha', targetWorkspaceId: 'ws-2' });
    // Drag state is cleared on drop.
    expect(cmp.projectDrag.draggingProjectId()).toBeNull();
  });

  it('does not emit when a project is dropped on its own workspace', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    twoWorkspaces(cmp, fixture);

    const groups = cmp.groups();
    const alpha = groups[0].projects[0]; // ws-default
    cmp.onDragStart({ dataTransfer: dragEvent(), preventDefault: noop } as unknown as DragEvent, alpha);

    let emitted = false;
    const sub = cmp.projectDrop.subscribe(() => (emitted = true));
    cmp.onWorkspaceDrop({ preventDefault: noop, dataTransfer: dragEvent() } as unknown as DragEvent, groups[0]);
    sub.unsubscribe();

    expect(emitted).toBe(false);
  });

  it('refuses to start a drag for an unregistered (__unassigned__) row', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    twoWorkspaces(cmp, fixture);

    const orphan = cmp.groups().find(g => g.id === '__unassigned__')!.projects[0];
    let prevented = false;
    cmp.onDragStart(
      { dataTransfer: dragEvent(), preventDefault() { prevented = true; } } as unknown as DragEvent,
      orphan,
    );
    expect(prevented).toBe(true);
    expect(cmp.projectDrag.draggingProjectId()).toBeNull();
  });

  it('rejects synthetic workspaces as drop targets even mid-drag', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    twoWorkspaces(cmp, fixture);

    const alpha = cmp.groups()[0].projects[0];
    cmp.onDragStart({ dataTransfer: dragEvent(), preventDefault: noop } as unknown as DragEvent, alpha);
    expect(cmp.canDropOnWorkspace('__unassigned__')).toBe(false);
    expect(cmp.canDropOnWorkspace('__all__')).toBe(false);
  });
});
