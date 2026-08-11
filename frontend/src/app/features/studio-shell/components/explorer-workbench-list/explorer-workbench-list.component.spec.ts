import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, vi } from 'vitest';
import type { WorkbenchCatalogue, WorkbenchListItem } from '../../../../models/project-docs.model';
import { ExplorerWorkbenchStateService } from '../../services/explorer-workbench-state.service';
import { ExplorerWorkbenchListComponent } from './explorer-workbench-list.component';

const STORAGE_KEY = 'atp.studio.explorer.workbenches.state.v2';

function item(
  id: string,
  status: WorkbenchListItem['status'],
  overrides: Partial<WorkbenchListItem> = {},
): WorkbenchListItem {
  return {
    id,
    key: `DEM-${id.toUpperCase()}`,
    title: id,
    summary: `${id} summary`,
    status,
    phase: 'testing',
    updatedAtUtc: '2026-07-12T10:00:00Z',
    entryPath: `docs/operations/${id}/index.html`,
    valid: true,
    error: null,
    sourceTaskKeys: [],
    relatedTaskKeys: [],
    pattern: 'concept',
    ...overrides,
  };
}

function catalogue(items: WorkbenchListItem[]): WorkbenchCatalogue {
  return { projectName: 'Demo', includesHistory: true, count: items.length, items };
}

async function mount(activeWorkbenchId: string | null = null) {
  await TestBed.configureTestingModule({
    imports: [ExplorerWorkbenchListComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();
  const fixture = TestBed.createComponent(ExplorerWorkbenchListComponent);
  fixture.componentRef.setInput('projectName', 'Demo');
  fixture.componentRef.setInput('activeWorkbenchId', activeWorkbenchId);
  fixture.detectChanges();
  return {
    fixture,
    component: fixture.componentInstance,
    http: TestBed.inject(HttpTestingController),
    state: TestBed.inject(ExplorerWorkbenchStateService),
  };
}

describe('ExplorerWorkbenchListComponent', () => {
  const scrollIntoView = vi.fn();

  beforeEach(() => {
    sessionStorage.removeItem(STORAGE_KEY);
    scrollIntoView.mockClear();
    Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', {
      configurable: true,
      value: scrollIntoView,
    });
  });

  it('groups the full catalogue into one-line tree rows with quiet trailing status', async () => {
    const { fixture, component, http } = await mount();
    component.toggle();
    http.expectOne(request => request.url === '/api/projects/Demo/workbenches'
      && request.params.get('history') === 'true')
      .flush(catalogue([
        item('pending', 'decision-pending', { openDecisionCount: 3 }),
        item('active', 'active', { pattern: 'ui' }),
        item('tracking', 'decided'),
        item('documented', 'documented'),
        item('discarded', 'archived'),
      ]));
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="studio-explorer-project-workbenches-Demo"]')?.textContent)
      .toContain('3');
    expect(root.querySelector('[data-testid="studio-explorer-workbench-group-Demo-needs-decision"]')?.textContent)
      .toContain('Needs a decision');
    expect(root.querySelector('[data-testid="studio-explorer-workbench-group-Demo-in-implementation"]')?.textContent)
      .toContain('In implementation');
    expect(root.querySelector('[data-testid="studio-explorer-workbench-Demo-pending"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="studio-explorer-workbench-Demo-active"]')).not.toBeNull();
    const pending = root.querySelector<HTMLElement>('[data-testid="studio-explorer-workbench-Demo-pending"]');
    expect(pending?.classList.contains('tree-row')).toBe(true);
    expect(pending?.textContent).toContain('3 open');
    expect(pending?.textContent).not.toContain('DEM-PENDING');
    expect(pending?.querySelector('.studio-workbench-topic__meta')).toBeNull();
    expect(root.querySelector('[data-testid="studio-explorer-workbench-status-Demo-pending"]')
      ?.getAttribute('data-status')).toBe('decision-pending');
    expect(root.querySelector('[data-testid="studio-explorer-workbench-history-Demo"]')
      ?.getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('[data-testid="studio-explorer-workbench-Demo-documented"]')).toBeNull();

    component.toggleGroup('history');
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="studio-explorer-workbench-Demo-documented"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="studio-explorer-workbench-Demo-discarded"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="studio-explorer-workbench-documented-history"]')?.textContent)
      .toContain('Documented');
    http.verify();
  });

  it('reveals only the active Dossier path and leaves foreign status groups untouched', async () => {
    const { fixture, component, http, state } = await mount('active');
    state.setGroupExpanded('Demo', 'needs-decision', false);
    state.setGroupExpanded('Demo', 'in-implementation', false);
    state.setGroupExpanded('Demo', 'history', true);

    http.expectOne(request => request.url === '/api/projects/Demo/workbenches'
      && request.params.get('history') === 'true')
      .flush(catalogue([item('pending', 'decision-pending'), item('active', 'active')]));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.expanded()).toBe(true);
    expect(component.groupExpanded('in-implementation')).toBe(true);
    expect(component.groupExpanded('needs-decision')).toBe(false);
    expect(component.groupExpanded('history')).toBe(true);
    const active = fixture.nativeElement.querySelector(
      '[data-testid="studio-explorer-workbench-Demo-active"]') as HTMLButtonElement;
    expect(active.getAttribute('aria-current')).toBe('page');
    expect(scrollIntoView).toHaveBeenCalledWith({ block: 'nearest', inline: 'nearest' });
    http.verify();
  });

  it('promotes the catalogue-owned living standard without opening the Dossiers branch', async () => {
    const guide = item('admin-design-guideline', 'living-standard', {
      key: 'AGT-W20',
      title: 'Admin Surface Design Guideline',
      entryPath: 'docs/operations/admin-design-guideline/index.html',
    });
    const { fixture, component, http } = await mount(guide.id);
    http.expectOne(request => request.url === '/api/projects/Demo/workbenches'
      && request.params.get('history') === 'true')
      .flush(catalogue([guide, item('active', 'active')]));
    await fixture.whenStable();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const styleGuide = root.querySelector(
      '[data-testid="studio-explorer-project-style-guide-Demo"]') as HTMLButtonElement;
    expect(component.expanded()).toBe(false);
    expect(styleGuide.textContent).toContain('Style Guide');
    expect(styleGuide.textContent).not.toContain('AGT-W20');
    expect(component.navTooltip(guide)).toContain(
      'Admin Surface Design Guideline\nAGT-W20\nconcept pattern, Living standard, updated',
    );
    expect(styleGuide.getAttribute('aria-current')).toBe('page');
    expect(styleGuide.getAttribute('aria-label')).toContain('Living standard');
    expect(root.querySelector('[data-testid="studio-explorer-workbench-Demo-admin-design-guideline"]'))
      .toBeNull();

    let opened: WorkbenchListItem | null = null;
    component.openWorkbench.subscribe(value => opened = value);
    styleGuide.click();
    expect(opened).toEqual(guide);
    http.verify();
  });

  it('opens the collapsed History path for a deep-linked documented Dossier', async () => {
    const { fixture, component, http, state } = await mount('documented');
    state.setGroupExpanded('Demo', 'needs-decision', false);
    state.setGroupExpanded('Demo', 'in-implementation', false);
    state.setGroupExpanded('Demo', 'history', false);
    http.expectOne(request => request.url === '/api/projects/Demo/workbenches'
      && request.params.get('history') === 'true')
      .flush(catalogue([item('documented', 'documented'), item('active', 'active')]));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.expanded()).toBe(true);
    expect(component.groupExpanded('history')).toBe(true);
    expect(component.groupExpanded('needs-decision')).toBe(false);
    expect(component.groupExpanded('in-implementation')).toBe(false);
    const documented = fixture.nativeElement.querySelector(
      '[data-testid="studio-explorer-workbench-Demo-documented"]') as HTMLButtonElement;
    expect(documented.getAttribute('aria-current')).toBe('page');
    http.verify();
  });
});
