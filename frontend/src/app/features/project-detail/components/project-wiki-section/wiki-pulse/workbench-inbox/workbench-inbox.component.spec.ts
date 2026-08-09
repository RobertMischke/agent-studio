import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { WikiLifecycleItem, WikiPulseLifecycle, WorkbenchCatalogue, WorkbenchListItem } from '../../../../../../models/project-docs.model';
import { WorkbenchInboxComponent } from './workbench-inbox.component';

function item(id: string, valid = true): WorkbenchListItem {
  return {
    id,
    key: valid ? `DEM-W${id === 'valid' ? '4' : '5'}` : null,
    title: id,
    summary: `${id} summary`,
    status: valid ? 'active' : 'invalid',
    phase: valid ? 'testing' : null,
    updatedAtUtc: new Date().toISOString(),
    entryPath: `docs/workbenches/${id}/index.html`,
    valid,
    error: valid ? null : 'Descriptor needs repair.',
    sourceTaskKeys: [],
    relatedTaskKeys: [],
  };
}

describe('WorkbenchInboxComponent', () => {
  it('groups lifecycle pages by state and opens Wiki pages or Workbenches directly', async () => {
    await TestBed.configureTestingModule({
      imports: [WorkbenchInboxComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchInboxComponent);
    const valid = item('valid');
    const invalid = item('invalid', false);
    const catalogue: WorkbenchCatalogue = {
      projectName: 'Demo', includesHistory: false, count: 2, items: [valid, invalid],
    };
    const page: WikiLifecycleItem = {
      relPath: 'concepts/indicator.md', title: 'Indicator alternatives', pageKind: 'exploration',
      state: 'review-requested', editedBy: 'Robert', editedAtUtc: new Date().toISOString(),
      history: [], workbenchId: null, valid: true, error: null,
    };
    const workbenchPage: WikiLifecycleItem = {
      relPath: valid.entryPath, title: valid.title, pageKind: 'workbench', state: 'in-progress',
      editedBy: 'Robert', editedAtUtc: valid.updatedAtUtc, history: [], workbenchId: valid.id,
      valid: true, error: null,
    };
    const invalidPage: WikiLifecycleItem = {
      relPath: invalid.entryPath, title: invalid.title, pageKind: 'workbench', state: 'review-requested',
      editedBy: null, editedAtUtc: invalid.updatedAtUtc, history: [], workbenchId: invalid.id,
      valid: false, error: invalid.error,
    };
    const lifecycle: WikiPulseLifecycle = {
      available: true, reason: null, count: 3, items: [page, invalidPage, workbenchPage],
    };
    fixture.componentRef.setInput('catalogue', catalogue);
    fixture.componentRef.setInput('lifecycle', lifecycle);
    let openedWorkbench: WorkbenchListItem | null = null;
    let openedPage: WikiLifecycleItem | null = null;
    fixture.componentInstance.openWorkbench.subscribe(value => openedWorkbench = value);
    fixture.componentInstance.openPage.subscribe(value => openedPage = value);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector(
      '[data-testid="project-wiki-lifecycle-group-review-requested"]')?.textContent).toContain('Indicator alternatives');
    expect(fixture.nativeElement.querySelector(
      '[data-testid="project-wiki-lifecycle-group-in-progress"]')?.textContent).toContain('valid');
    expect(fixture.nativeElement.querySelector(
      '[data-testid="project-wiki-lifecycle-group-invalid"]')?.textContent).toContain('invalid');
    expect(fixture.nativeElement.querySelector(
      '[data-testid="project-wiki-lifecycle-group-review-requested"]')?.textContent).not.toContain('Descriptor needs repair.');
    const pageButton = fixture.nativeElement.querySelector(
      '[data-testid="project-wiki-lifecycle-open-concepts/indicator.md"]') as HTMLButtonElement;
    const validButton = fixture.nativeElement.querySelector(
      `[data-testid="project-wiki-lifecycle-open-${valid.entryPath}"]`) as HTMLButtonElement;
    const invalidButton = fixture.nativeElement.querySelector(
      `[data-testid="project-wiki-lifecycle-open-${invalid.entryPath}"]`) as HTMLButtonElement;
    expect(validButton.disabled).toBe(false);
    expect(fixture.nativeElement.querySelector(
      '[data-testid="project-wiki-lifecycle-key-DEM-W4"]')?.textContent).toContain('DEM-W4');
    expect(invalidButton.disabled).toBe(true);
    expect(invalidButton.textContent).toContain('Descriptor needs repair.');
    pageButton.click();
    expect(openedPage).toEqual(page);
    validButton.click();
    expect(openedWorkbench).toEqual(valid);
  });
});
