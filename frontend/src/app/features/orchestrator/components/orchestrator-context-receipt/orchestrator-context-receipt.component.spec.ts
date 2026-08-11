import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { OrchestratorContextReceiptComponent } from './orchestrator-context-receipt.component';

describe('OrchestratorContextReceiptComponent', () => {
  async function create(receipt: object) {
    await TestBed.configureTestingModule({
      imports: [OrchestratorContextReceiptComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorContextReceiptComponent);
    fixture.componentRef.setInput('receipt', receipt);
    fixture.detectChanges();
    return fixture;
  }

  it('discloses included, excerpted, and unresolved source truth', async () => {
    const fixture = await create({
      scope: 'project', contextKey: 'project:Demo', taskKey: null,
      includedBlocks: ['file:Demo/README.md'], capturedAt: '2026-08-10T10:00:00Z',
      receiptId: 'rcp_1234567890abcdef', userTurnId: 'user_1',
      budget: { automaticSoftCapTokens: 4000, automaticHardCapTokens: 6000, totalHardCapTokens: 8000, estimatedIncludedTokens: 1720 },
      sources: [
        { sourceId: 'file:Demo/README.md', kind: 'repository-file', revision: '0123456789abcdef', sha256: 'abc', freshness: 'current', includedCharacters: 4000, estimatedTokens: 1000, status: 'included' },
        { sourceId: 'page:Demo/concept.md', kind: 'page', revision: null, sha256: 'def', freshness: 'current', includedCharacters: 2800, estimatedTokens: 700, status: 'excerpted', reason: 'Bounded to the submitted budget.' },
        { sourceId: 'task:Demo/DEMO-9/bundle', kind: 'task-bundle', revision: null, sha256: null, freshness: 'unknown', includedCharacters: 0, estimatedTokens: 0, status: 'unresolved', reason: 'Task not found.' },
      ],
    });

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="orch-context-inspector"]')).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-testid="orch-context-inspect-toggle"]')!.click();
    fixture.detectChanges();

    const inspector = root.querySelector('[data-testid="orch-context-inspector"]')!;
    expect(inspector.textContent).toContain('Included');
    expect(inspector.textContent).toContain('Excerpted');
    expect(inspector.textContent).toContain('Unresolved');
    expect(inspector.textContent).toContain('Task not found.');
  });

  it('shows resolved file, commit, and selected-hunk diff sources', async () => {
    const fixture = await create({
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

    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('[data-testid="orch-context-inspect-toggle"]')!.click();
    fixture.detectChanges();

    expect(root.querySelectorAll('[data-testid="orch-answer-context-source"]')).toHaveLength(3);
    expect(root.textContent).toContain('Repository File');
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
