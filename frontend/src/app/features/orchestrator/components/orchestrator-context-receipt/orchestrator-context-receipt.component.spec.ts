import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { OrchestratorContextReceiptComponent } from './orchestrator-context-receipt.component';

describe('OrchestratorContextReceiptComponent', () => {
  it('shows resolved file, commit and selected-hunk diff sources', async () => {
    await TestBed.configureTestingModule({
      imports: [OrchestratorContextReceiptComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorContextReceiptComponent);
    fixture.componentRef.setInput('receipt', {
      scope: 'project',
      contextKey: 'project:Agent Studio',
      includedBlocks: ['repository-file', 'commit', 'diff'],
      capturedAt: '2026-08-10T08:00:00Z',
      sources: [
        source('repository-file', 'file:Agent Studio/docs/README.md'),
        source('commit', 'commit:Agent Studio/0123456789012345678901234567890123456789'),
        source('diff', 'diff:Agent Studio/0123456789012345678901234567890123456789:src/app.ts#L7-L12'),
      ],
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('[data-testid="orch-answer-context-source"]')).toHaveLength(3);
    expect(root.textContent).toContain('repository-file');
    expect(root.textContent).toContain('#L7-L12');
    expect(root.textContent).toContain('012345678901');
  });
});

function source(kind: string, sourceId: string) {
  return {
    sourceId,
    kind,
    revision: '0123456789012345678901234567890123456789',
    freshness: 'immutable-revision',
    includedCharacters: 320,
    estimatedTokens: 80,
    status: 'included',
  };
}
