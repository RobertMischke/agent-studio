import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { PipelineTokenUsageComponent } from './pipeline-token-usage.component';
import type {
  PipelineModelTokenUsage,
  PipelineModelUsageSummary,
  PipelineRunTokenUsage,
} from '../../../../task-pipeline';

function model(
  name: string,
  total: number,
  cost: number,
  known = true,
  steps = 1,
): PipelineModelTokenUsage {
  return {
    model: name,
    modelKnown: known,
    steps,
    inputTokens: total,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    totalTokens: total,
    costUsd: cost,
  };
}

function run(attempt: number, current: boolean, models: PipelineModelTokenUsage[]): PipelineRunTokenUsage {
  return {
    attempt,
    current,
    startedAt: '2026-06-09T10:00:00Z',
    completedAt: '2026-06-09T10:05:00Z',
    models,
    totalTokens: models.reduce((a, m) => a + m.totalTokens, 0),
    totalCostUsd: models.reduce((a, m) => a + m.costUsd, 0),
    anyModelUnknown: models.some((m) => m.totalTokens > 0 && !m.modelKnown),
  };
}

const SUMMARY: PipelineModelUsageSummary = {
  runs: [
    run(1, false, [model('claude-haiku-4-5', 1_200_000, 2)]),
    run(2, true, [
      model('claude-haiku-4-5', 1_200_000, 2),
      model('claude-opus-4-8', 110_000, 0.75),
    ]),
  ],
  totalByModel: [
    model('claude-haiku-4-5', 2_400_000, 4, true, 3),
    model('claude-opus-4-8', 110_000, 0.75),
  ],
  totalTokens: 2_510_000,
  totalCostUsd: 4.75,
  anyModelUnknown: false,
};

function setup(summary: PipelineModelUsageSummary | null) {
  TestBed.configureTestingModule({
    imports: [PipelineTokenUsageComponent],
    providers: [provideZonelessChangeDetection()],
  });
  const fixture = TestBed.createComponent(PipelineTokenUsageComponent);
  fixture.componentRef.setInput('summary', summary);
  fixture.detectChanges();
  return fixture;
}

const root = (fixture: { nativeElement: HTMLElement }) => fixture.nativeElement as HTMLElement;

describe('PipelineTokenUsageComponent', () => {
  it('renders nothing when the summary is null', () => {
    const fixture = setup(null);
    expect(root(fixture).querySelector('[data-testid="pipeline-token-usage"]')).toBeNull();
  });

  it('renders nothing when there are no runs', () => {
    const fixture = setup({ runs: [], totalByModel: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false });
    expect(root(fixture).querySelector('[data-testid="pipeline-token-usage"]')).toBeNull();
  });

  it('renders one card per run with a model row each', () => {
    const fixture = setup(SUMMARY);
    const runs = root(fixture).querySelectorAll('[data-testid="pipeline-token-usage-run"]');
    expect(runs.length).toBe(2);

    // Run #2 is the current run and carries both model rows.
    const current = runs[1];
    expect(current.getAttribute('data-current')).toBe('true');
    expect(current.querySelector('[data-testid="pipeline-token-usage-run-current"]')).not.toBeNull();
    expect(
      current.querySelectorAll('[data-testid="pipeline-token-usage-run-model"]').length,
    ).toBe(2);
  });

  it('renders a grand-total card summing every model over all runs', () => {
    const fixture = setup(SUMMARY);
    const total = root(fixture).querySelector('[data-testid="pipeline-token-usage-total"]');
    expect(total).not.toBeNull();

    const totalModels = total!.querySelectorAll('[data-testid="pipeline-token-usage-total-model"]');
    expect(totalModels.length).toBe(2);

    const grand = root(fixture).querySelector('[data-testid="pipeline-token-usage-grand-total"]');
    expect(grand).not.toBeNull();
    // 2.51M tokens -> formatTokens renders "2.51M".
    expect(
      root(fixture).querySelector('[data-testid="pipeline-token-usage-grand-total-tokens"]')?.textContent,
    ).toContain('2.51M');
    expect(
      root(fixture).querySelector('[data-testid="pipeline-token-usage-grand-total-cost"]')?.textContent,
    ).toContain('$4.75');
  });

  it('renders "n/a" cost for a model with no price on file', () => {
    const summary: PipelineModelUsageSummary = {
      runs: [run(1, true, [model('unpriced-test-model', 700, 0, false)])],
      totalByModel: [model('unpriced-test-model', 700, 0, false)],
      totalTokens: 700,
      totalCostUsd: 0,
      anyModelUnknown: true,
    };
    const fixture = setup(summary);
    const modelRow = root(fixture).querySelector('[data-testid="pipeline-token-usage-total-model"]');
    expect(modelRow?.textContent).toContain('n/a');
    // The grand-total cost carries the unknown-model asterisk.
    expect(
      root(fixture).querySelector('[data-testid="pipeline-token-usage-grand-total-cost"]')?.textContent,
    ).toContain('*');
  });
});
