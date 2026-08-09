import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, vi } from 'vitest';
import type { WorkbenchCatalogue, WorkbenchListItem } from '../../../../models/project-docs.model';
import { ExplorerWorkbenchListComponent } from './explorer-workbench-list.component';

function item(id: string, status: WorkbenchListItem['status']): WorkbenchListItem {
  return {
    id,
    key: `DEM-W${id === 'active' ? '4' : '5'}`,
    title: id,
    summary: `${id} summary`,
    status,
    phase: 'testing',
    updatedAtUtc: '2026-07-12T10:00:00Z',
    entryPath: `docs/workbenches/${id}/index.html`,
    valid: true,
    error: null,
    sourceTaskKeys: [],
    relatedTaskKeys: [],
  };
}

function catalogue(items: WorkbenchListItem[], includesHistory: boolean): WorkbenchCatalogue {
  return { projectName: 'Demo', includesHistory, count: items.length, items };
}

describe('ExplorerWorkbenchListComponent', () => {
  const scrollIntoView = vi.fn();

  beforeEach(() => {
    window.localStorage.removeItem('atp.studio.explorer.workbenches.expanded.v1');
    scrollIntoView.mockClear();
    Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', {
      configurable: true,
      value: scrollIntoView,
    });
  });

  it('loads history separately and shows the empty state after filtering current items', async () => {
    await TestBed.configureTestingModule({
      imports: [ExplorerWorkbenchListComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ExplorerWorkbenchListComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);

    fixture.componentInstance.toggle();
    http.expectOne('/api/projects/Demo/workbenches').flush(catalogue([item('active', 'active')], false));
    fixture.detectChanges();
    fixture.componentInstance.toggleHistory();
    http.expectOne(request => request.url === '/api/projects/Demo/workbenches'
      && request.params.get('history') === 'true')
      .flush(catalogue([item('active', 'active')], true));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('No settled Workbenches');
    expect(fixture.componentInstance.settledHistory()).toEqual([]);
    http.verify();
  });

  it('renders and emits only settled entries in the history section', async () => {
    await TestBed.configureTestingModule({
      imports: [ExplorerWorkbenchListComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ExplorerWorkbenchListComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();
    fixture.componentInstance.expanded.set(true);
    fixture.componentInstance.historyOpen.set(true);
    const archived = item('archived', 'archived');
    fixture.componentInstance.historyCatalogue.set(catalogue([item('active', 'active'), archived], true));
    let opened: WorkbenchListItem | null = null;
    fixture.componentInstance.openWorkbench.subscribe(value => opened = value);
    fixture.detectChanges();

    const archivedButton = fixture.nativeElement.querySelector(
      '[data-testid="studio-explorer-workbench-Demo-archived"]') as HTMLButtonElement;
    expect(archivedButton).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="studio-explorer-workbench-Demo-active"]')).toBeNull();
    archivedButton.click();
    expect(opened).toEqual(archived);
  });

  it('selects and reveals the active Workbench while keeping its disclosure parent neutral', async () => {
    await TestBed.configureTestingModule({
      imports: [ExplorerWorkbenchListComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ExplorerWorkbenchListComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.componentRef.setInput('activeWorkbenchId', 'active');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);

    http.expectOne('/api/projects/Demo/workbenches')
      .flush(catalogue([item('active', 'active'), item('another', 'active')], false));
    await fixture.whenStable();
    fixture.detectChanges();

    const active = fixture.nativeElement.querySelector(
      '[data-testid="studio-explorer-workbench-Demo-active"]') as HTMLButtonElement;
    const another = fixture.nativeElement.querySelector(
      '[data-testid="studio-explorer-workbench-Demo-another"]') as HTMLButtonElement;
    const disclosure = fixture.nativeElement.querySelector(
      '[data-testid="studio-explorer-project-workbenches-Demo"]') as HTMLButtonElement;

    expect(fixture.componentInstance.expanded()).toBe(true);
    expect(active.classList.contains('studio-workbench-topic--active')).toBe(true);
    expect(active.getAttribute('aria-current')).toBe('page');
    expect(fixture.componentInstance.navTooltip(item('active', 'active'))).toContain('DEM-W4');
    expect(another.getAttribute('aria-current')).toBeNull();
    expect(disclosure.classList.contains('tree-row--active')).toBe(false);
    expect(disclosure.getAttribute('aria-current')).toBeNull();
    expect(scrollIntoView).toHaveBeenCalledWith({ block: 'nearest', inline: 'nearest' });
    expect(JSON.parse(window.localStorage.getItem(
      'atp.studio.explorer.workbenches.expanded.v1') ?? '[]')).toContain('Demo');
    http.verify();
  });

  it('persists the Workbenches disclosure independently for each project', async () => {
    await TestBed.configureTestingModule({
      imports: [ExplorerWorkbenchListComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ExplorerWorkbenchListComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);

    fixture.componentInstance.toggle();
    http.expectOne('/api/projects/Demo/workbenches').flush(catalogue([], false));
    fixture.detectChanges();

    expect(fixture.componentInstance.expanded()).toBe(true);
    expect(JSON.parse(window.localStorage.getItem(
      'atp.studio.explorer.workbenches.expanded.v1') ?? '[]')).toEqual(['Demo']);

    fixture.componentRef.setInput('projectName', 'Other');
    fixture.detectChanges();
    expect(fixture.componentInstance.expanded()).toBe(false);

    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();
    expect(fixture.componentInstance.expanded()).toBe(true);
    http.expectOne('/api/projects/Demo/workbenches').flush(catalogue([], false));
    http.verify();
  });

  it('opens History when a deep-linked settled Workbench is outside the current catalogue', async () => {
    await TestBed.configureTestingModule({
      imports: [ExplorerWorkbenchListComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ExplorerWorkbenchListComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.componentRef.setInput('activeWorkbenchId', 'archived');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const archived = item('archived', 'archived');

    http.expectOne('/api/projects/Demo/workbenches')
      .flush(catalogue([item('active', 'active')], false));
    http.expectOne(request => request.url === '/api/projects/Demo/workbenches'
      && request.params.get('history') === 'true')
      .flush(catalogue([item('active', 'active'), archived], true));
    await fixture.whenStable();
    fixture.detectChanges();

    const archivedButton = fixture.nativeElement.querySelector(
      '[data-testid="studio-explorer-workbench-Demo-archived"]') as HTMLButtonElement;
    expect(fixture.componentInstance.historyOpen()).toBe(true);
    expect(archivedButton.getAttribute('aria-current')).toBe('page');
    expect(archivedButton.classList.contains('studio-workbench-topic--active')).toBe(true);
    http.verify();
  });
});
