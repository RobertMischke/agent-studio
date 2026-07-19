import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { WorkbenchCatalogue, WorkbenchListItem } from '../../../../../../models/project-docs.model';
import { WorkbenchInboxComponent } from './workbench-inbox.component';

function item(id: string, valid = true): WorkbenchListItem {
  return {
    id,
    title: id,
    summary: `${id} summary`,
    status: valid ? 'active' : 'invalid',
    phase: valid ? 'testing' : null,
    updatedAtUtc: new Date().toISOString(),
    entryPath: `docs/workbenches/${id}/index.html`,
    valid,
    error: valid ? null : 'Descriptor needs repair.',
    sourceTaskKeys: [],
  };
}

describe('WorkbenchInboxComponent', () => {
  it('renders catalogue state, disables invalid entries, and emits valid selections', async () => {
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
    fixture.componentRef.setInput('catalogue', catalogue);
    let opened: WorkbenchListItem | null = null;
    fixture.componentInstance.openWorkbench.subscribe(value => opened = value);
    fixture.detectChanges();

    const validButton = fixture.nativeElement.querySelector(
      '[data-testid="project-wiki-pulse-workbench-valid"]') as HTMLButtonElement;
    const invalidButton = fixture.nativeElement.querySelector(
      '[data-testid="project-wiki-pulse-workbench-invalid"]') as HTMLButtonElement;
    expect(validButton.disabled).toBe(false);
    expect(invalidButton.disabled).toBe(true);
    expect(invalidButton.textContent).toContain('Descriptor needs repair.');
    validButton.click();
    expect(opened).toEqual(valid);
  });
});
