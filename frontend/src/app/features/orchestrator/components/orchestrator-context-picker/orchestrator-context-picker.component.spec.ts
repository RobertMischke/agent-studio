import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import type { OrchestratorContextSourceOption } from '../../models/orchestrator-context-source.model';
import { OrchestratorContextPickerComponent } from './orchestrator-context-picker.component';

const CURRENT: OrchestratorContextSourceOption = {
  id: 'page:Demo:page:Demo/concepts/context.md',
  category: 'current',
  label: 'Context workspace',
  detail: 'Page · concepts/context.md',
  estimateTokens: 1_200,
  reference: { kind: 'page', reference: 'page:Demo/concepts/context.md', projectId: 'Demo' },
};

describe('OrchestratorContextPickerComponent', () => {
  async function fixture() {
    await TestBed.configureTestingModule({
      imports: [OrchestratorContextPickerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const result = TestBed.createComponent(OrchestratorContextPickerComponent);
    result.componentRef.setInput('project', 'Demo');
    result.componentRef.setInput('automaticLabel', 'Page · Context workspace');
    result.componentRef.setInput('currentSource', CURRENT);
    result.detectChanges();
    return result;
  }

  it('prioritizes the current tab and emits its stable typed reference', async () => {
    const result = await fixture();
    const added = vi.fn();
    result.componentInstance.attachmentAdded.subscribe(added);

    const root = result.nativeElement as HTMLElement;
    result.componentInstance.show();
    result.detectChanges();
    root.querySelector<HTMLButtonElement>('[data-testid="orch-context-current-source"]')!.click();

    expect(added).toHaveBeenCalledWith(CURRENT);
  });

  it('creates stable typed references for free task, Wiki, and repository input', async () => {
    const result = await fixture();
    const added = vi.fn();
    result.componentInstance.attachmentAdded.subscribe(added);

    for (const value of ['AGT-2506', 'wiki:operations/setup.md', 'frontend/src/app.ts']) {
      result.componentInstance.query.set(value);
      result.componentInstance.addTypedReference();
    }

    expect(added.mock.calls.map(call => call[0].reference)).toEqual([
      { kind: 'task', reference: 'AGT-2506', projectId: 'Demo' },
      { kind: 'page', reference: 'page:Demo/operations/setup.md', projectId: 'Demo' },
      { kind: 'repository-file', reference: 'frontend/src/app.ts', projectId: 'Demo' },
    ]);
  });
});
