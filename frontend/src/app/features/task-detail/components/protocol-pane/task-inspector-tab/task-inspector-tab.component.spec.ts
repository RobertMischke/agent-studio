import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskInspectorTabComponent } from './task-inspector-tab.component';

describe('TaskInspectorTabComponent', () => {
  let fixture: ComponentFixture<TaskInspectorTabComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [TaskInspectorTabComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(TaskInspectorTabComponent);
  });

  it('renders the task markdown and quiet refinement metadata in chronological input order', async () => {
    fixture.componentRef.setInput('promptMarkdown', '# Original task\n\nBuild the tab.');
    fixture.componentRef.setInput('refinements', [
      {
        id: 'operator-1',
        at: '2026-07-28T09:05:00Z',
        actor: 'operator',
        reason: 'steer follow-up',
        markdown: 'Keep the layout calm.',
        source: 'run-log',
        runIndex: 2,
      },
      {
        id: 'system-1',
        at: '2026-07-28T09:10:00Z',
        actor: 'system',
        reason: 'Missing regression coverage',
        markdown: 'Add a browser test.',
        source: 'orchestrator-history',
        runIndex: null,
      },
    ]);
    fixture.detectChanges();
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="task-tab-prompt"]')?.textContent).toContain('Original task');
    const entries = [...element.querySelectorAll('[data-testid="task-refinement-entry"]')];
    expect(entries).toHaveLength(2);
    expect(entries[0].textContent).toContain('Operator');
    expect(entries[0].textContent).toContain('steer follow-up');
    expect(entries[1].textContent).toContain('System');
    expect(entries[1].textContent).toContain('Missing regression coverage');
  });

  it('shows the enrichment decisions and token ledger beside the unchanged prompt', async () => {
    fixture.componentRef.setInput('promptMarkdown', '# Original task\n\nThis remains readable.');
    fixture.componentRef.setInput('enrichmentReport', {
      schemaVersion: '1.0',
      enrichmentId: 'enrichment-1',
      generatedAtUtc: '2026-07-28T09:00:00Z',
      status: 'enriched',
      originalPromptSha256: 'aaa',
      enrichedPromptSha256: 'bbb',
      policy: {
        id: 'prompt-enrichment',
        version: '1',
        projectEnabled: true,
        selector: 'constraint-selector-v4-token-economy',
        tokenizer: 'character-estimate-v1',
        tokenBudget: 1500,
        optionalBlockLimit: 2,
      },
      detectedAreas: ['frontend', 'delegation'],
      candidates: [
        {
          id: 'delegation-economy',
          title: 'Keep delegation bounded',
          source: 'AGENTS.md',
          signals: ['delegation'],
          decision: 'appended',
          reason: 'matched-task-area',
          estimatedTokens: 41,
        },
      ],
      appendedBlocks: [
        {
          id: 'delegation-economy',
          title: 'Keep delegation bounded',
          source: 'AGENTS.md',
          revision: '1',
          digestSha256: 'ccc',
          tier: 'optional',
          order: 1,
          estimatedTokens: 41,
          exactContent: '- **Keep delegation bounded** (`delegation-economy`)',
        },
      ],
      tokens: {
        tokenizer: 'character-estimate-v1',
        original: 120,
        appended: 41,
        final: 161,
        preprocessingInput: 0,
        preprocessingOutput: 0,
        preprocessingCacheRead: 0,
        preprocessingCacheCreation: 0,
      },
      cost: {
        currency: 'USD',
        selectorUsd: 0,
        appendedInputUsd: 0.0002,
        estimateModel: 'test-model',
      },
      timingMs: 3,
      warnings: [],
      errors: [],
    });
    fixture.detectChanges();
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="task-tab-prompt"]')?.textContent)
      .toContain('This remains readable.');
    const report = element.querySelector('[data-testid="enrichment-report"]');
    expect(report?.textContent).toContain('Enrichment report');
    expect(report?.textContent).toContain('Enriched');
    expect(report?.textContent).toContain('frontend');
    expect(report?.textContent).toContain('Keep delegation bounded');
    expect(element.querySelector('[data-testid="enrichment-ledger"]')?.textContent)
      .toContain('Selector: 0 tokens');
    expect(report?.textContent).toContain('+41');
  });
});
