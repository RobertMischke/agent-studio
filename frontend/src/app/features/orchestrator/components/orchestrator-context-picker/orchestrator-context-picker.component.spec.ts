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
  key: 'CTX-W1',
  label: 'Context workspace',
  detail: 'Page · concepts/context.md',
  estimateTokens: 1_200,
  reference: { kind: 'page', reference: 'page:Demo/concepts/context.md', projectId: 'Demo' },
};

describe('OrchestratorContextPickerComponent', () => {
  async function fixture(currentSource: OrchestratorContextSourceOption = CURRENT) {
    await TestBed.configureTestingModule({
      imports: [OrchestratorContextPickerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const result = TestBed.createComponent(OrchestratorContextPickerComponent);
    result.componentRef.setInput('project', 'Demo');
    result.componentRef.setInput('automaticLabel', 'Context workspace with a deliberately long title');
    result.componentRef.setInput('automaticKey', 'AGT-W34');
    result.componentRef.setInput('automaticTypeLabel', 'Dossier');
    result.componentRef.setInput('automaticIcon', 'eye');
    result.componentRef.setInput('currentSource', currentSource);
    result.detectChanges();
    return result;
  }

  it('prioritizes the current tab and emits its stable typed reference', async () => {
    const result = await fixture();
    const added = vi.fn();
    result.componentInstance.attachmentAdded.subscribe(added);

    const root = result.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('[data-testid="orch-add-context"]')!.click();
    result.detectChanges();
    root.querySelector<HTMLButtonElement>('[data-testid="orch-context-add-current"]')!.click();

    expect(added).toHaveBeenCalledWith(CURRENT);
  });

  it('prefers the stable key and falls back to the full title for ellipsis styling', async () => {
    const result = await fixture();
    const root = result.nativeElement as HTMLElement;

    expect(root.querySelector('[data-testid="orch-current-tab-label"]')?.textContent?.trim())
      .toBe('AGT-W34');
    expect(root.querySelector('[data-testid="orch-current-tab-chip"]')?.textContent)
      .not.toContain('deliberately long title');
    expect(result.componentInstance.automaticTooltip())
      .toContain('Context workspace with a deliberately long title');

    result.componentRef.setInput('automaticKey', null);
    result.detectChanges();
    expect(root.querySelector('[data-testid="orch-current-tab-label"]')?.textContent?.trim())
      .toBe('Context workspace with a deliberately long title');
  });

  it('preserves the selected file and line range in a current diff reference', async () => {
    const sha = '0123456789012345678901234567890123456789';
    const currentDiff: OrchestratorContextSourceOption = {
      id: `diff:Demo:Demo:${sha}:frontend/src/app/app.ts:8-16`,
      category: 'current',
      label: 'frontend/src/app/app.ts',
      detail: `Diff · ${sha.slice(0, 8)} · L8-L16`,
      estimateTokens: 900,
      reference: {
        kind: 'diff',
        reference: sha,
        revision: sha,
        projectId: 'Demo',
        repositoryId: 'Demo',
        path: 'frontend/src/app/app.ts',
        lineRanges: [{ startLine: 8, endLine: 16 }],
      },
    };
    const result = await fixture(currentDiff);
    const added = vi.fn();
    result.componentInstance.attachmentAdded.subscribe(added);

    result.componentInstance.add(currentDiff);

    expect(added).toHaveBeenCalledWith(expect.objectContaining({
      reference: expect.objectContaining({
        kind: 'diff',
        path: 'frontend/src/app/app.ts',
        lineRanges: [{ startLine: 8, endLine: 16 }],
      }),
    }));
  });

  it('renders removable chips and an honest send-time estimate', async () => {
    const result = await fixture();
    result.componentRef.setInput('attachments', [CURRENT]);
    result.detectChanges();
    const removed = vi.fn();
    result.componentInstance.attachmentRemoved.subscribe(removed);

    const root = result.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="orch-context-estimate"]')?.textContent)
      .toContain('~2.8k');
    expect(root.querySelector('[data-testid="orch-context-estimate"]')?.textContent)
      .not.toContain('resolved when you send');
    expect(result.componentInstance.contextMetaTooltip())
      .toBe('2 sources · about 2,800 tokens · resolved when you send');
    expect(root.querySelector(`[data-testid="orch-context-chip-${CURRENT.id}"]`)?.textContent)
      .toContain('CTX-W1');
    root.querySelector<HTMLButtonElement>('[aria-label^="Remove Context workspace"]')!.click();
    expect(removed).toHaveBeenCalledWith(CURRENT.id);
  });
});
