import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { WorkbenchCatalogue, WorkbenchListItem } from '../../../../models/project-docs.model';
import { ExplorerWorkbenchListComponent } from './explorer-workbench-list.component';

function item(id: string, status: WorkbenchListItem['status']): WorkbenchListItem {
  return {
    id,
    title: id,
    summary: `${id} summary`,
    status,
    phase: 'testing',
    updatedAtUtc: '2026-07-12T10:00:00Z',
    entryPath: `docs/workbenches/${id}/index.html`,
    valid: true,
    error: null,
    sourceTaskKeys: [],
  };
}

function catalogue(items: WorkbenchListItem[], includesHistory: boolean): WorkbenchCatalogue {
  return { projectName: 'Demo', includesHistory, count: items.length, items };
}

describe('ExplorerWorkbenchListComponent', () => {
  beforeEach(() => window.localStorage.clear());

  it('reveals, selects, and scrolls to the active Workbench without routine metadata noise', async () => {
    await TestBed.configureTestingModule({
      imports: [ExplorerWorkbenchListComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const scrollIntoView = vi.fn();
    const originalScrollIntoView = HTMLElement.prototype.scrollIntoView;
    HTMLElement.prototype.scrollIntoView = scrollIntoView;
    const fixture = TestBed.createComponent(ExplorerWorkbenchListComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.componentRef.setInput('activeWorkbenchId', 'active');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);

    http.expectOne('/api/projects/Demo/workbenches')
      .flush(catalogue([item('active', 'active'), item('pending', 'decision-pending')], false));
    fixture.detectChanges();
    await Promise.resolve();

    const root: HTMLElement = fixture.nativeElement;
    const group = root.querySelector<HTMLElement>('[data-testid="studio-explorer-project-workbenches-Demo"]');
    const active = root.querySelector<HTMLElement>('[data-testid="studio-explorer-workbench-Demo-active"]');
    const pending = root.querySelector<HTMLElement>('[data-testid="studio-explorer-workbench-Demo-pending"]');
    expect(fixture.componentInstance.expanded()).toBe(true);
    expect(group?.classList.contains('tree-row--active')).toBe(true);
    expect(group?.getAttribute('aria-current')).toBeNull();
    expect(active?.classList.contains('studio-workbench-topic--active')).toBe(true);
    expect(active?.getAttribute('aria-current')).toBe('page');
    expect(active?.getAttribute('aria-label')).toBe('active, current Workbench');
    expect(active?.textContent).not.toContain('testing');
    expect(active?.textContent).not.toContain('today');
    expect(pending?.textContent).toContain('Decision pending');
    expect(scrollIntoView).toHaveBeenCalledWith({
      behavior: 'smooth',
      block: 'nearest',
      inline: 'nearest',
    });
    expect(window.localStorage.getItem('atp.studio.explorer.workbenchSections'))
      .toBe('["Demo"]');
    http.verify();
    HTMLElement.prototype.scrollIntoView = originalScrollIntoView;
  });

  it('restores an expanded branch for its project and keeps other projects collapsed', async () => {
    window.localStorage.setItem('atp.studio.explorer.workbenchSections', '["Demo"]');
    await TestBed.configureTestingModule({
      imports: [ExplorerWorkbenchListComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const http = TestBed.inject(HttpTestingController);
    const demo = TestBed.createComponent(ExplorerWorkbenchListComponent);
    demo.componentRef.setInput('projectName', 'Demo');
    demo.detectChanges();
    http.expectOne('/api/projects/Demo/workbenches').flush(catalogue([], false));
    demo.detectChanges();

    const other = TestBed.createComponent(ExplorerWorkbenchListComponent);
    other.componentRef.setInput('projectName', 'Other');
    other.detectChanges();

    expect(demo.componentInstance.expanded()).toBe(true);
    expect(other.componentInstance.expanded()).toBe(false);
    http.expectNone('/api/projects/Other/workbenches');
    http.verify();
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
    fixture.componentInstance.toggle();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/projects/Demo/workbenches')
      .flush(catalogue([], false));
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
});
