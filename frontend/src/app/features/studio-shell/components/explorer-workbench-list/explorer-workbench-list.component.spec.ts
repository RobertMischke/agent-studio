import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
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
});
