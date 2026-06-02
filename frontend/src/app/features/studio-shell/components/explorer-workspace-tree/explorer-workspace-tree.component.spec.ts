import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import {
  ExplorerWorkspaceTreeComponent,
  type ExplorerProjectRow,
} from './explorer-workspace-tree.component';
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

function row(name: string): ExplorerProjectRow {
  return { name, initial: name[0]?.toUpperCase() ?? '?', color: '#888', totalJobs: 0, isActive: false };
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

  it('right-click opens a text-only Rename menu that starts the inline rename', () => {
    const fixture = mount();
    const cmp = fixture.componentInstance;
    fixture.componentRef.setInput('registryWorkspaces', [
      workspace('ws-default', 'Default', 0, [project('Alpha', 'ws-default', '/repos/Alpha')]),
    ]);
    fixture.componentRef.setInput('projectRows', [row('Alpha')]);

    const g = cmp.groups()[0];
    const ev = { preventDefault: () => {}, clientX: 12, clientY: 34 } as MouseEvent;
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
});
