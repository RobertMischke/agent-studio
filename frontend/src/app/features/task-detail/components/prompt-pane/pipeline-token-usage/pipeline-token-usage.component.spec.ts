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
const all = (el: HTMLElement, sel: string) => el.querySelectorAll(`[data-testid="${sel}"]`);
const one = (el: HTMLElement, sel: string) => el.querySelector(`[data-testid="${sel}"]`);

describe('PipelineTokenUsageComponent', () => {
  it('renders nothing when the summary is null', () => {
    const fixture = setup(null);
    expect(one(root(fixture), 'pipeline-token-usage')).toBeNull();
  });

  it('renders nothing when there are no runs', () => {
    const fixture = setup({ runs: [], totalByModel: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false });
    expect(one(root(fixture), 'pipeline-token-usage')).toBeNull();
  });

  it('TASK TOTAL SUM is collapsed by default: lifetime total shows, model split hidden', () => {
    const fixture = setup(SUMMARY);
    // The total toggle line is always visible with the lifetime tokens + cost.
    expect(one(root(fixture), 'pipeline-token-usage-total')).not.toBeNull();
    expect(one(root(fixture), 'pipeline-token-usage-grand-total-tokens')?.textContent).toContain('2.51M');
    expect(one(root(fixture), 'pipeline-token-usage-grand-total-cost')?.textContent).toContain('$4.75');
    // Collapsed: the all-runs-by-model breakdown is not in the DOM yet.
    expect(all(root(fixture), 'pipeline-token-usage-total-model').length).toBe(0);
  });

  it('expanding TASK TOTAL SUM reveals the all-runs-by-model breakdown inline', () => {
    const fixture = setup(SUMMARY);
    fixture.componentInstance.toggleSummary();
    fixture.detectChanges();
    expect(all(root(fixture), 'pipeline-token-usage-total-model').length).toBe(2);
  });

  it('renders one collapsible row per run, every run collapsed by default', () => {
    const fixture = setup(SUMMARY);
    const runs = all(root(fixture), 'pipeline-token-usage-run');
    expect(runs.length).toBe(2);
    // No per-model rows until a run is expanded.
    expect(all(root(fixture), 'pipeline-token-usage-run-model').length).toBe(0);

    // Newest-first: the current run (#2) renders on top and carries the badge.
    const first = runs[0];
    expect(first.getAttribute('data-current')).toBe('true');
    expect(first.querySelector('[data-testid="pipeline-token-usage-run-current"]')).not.toBeNull();
  });

  it('expanding a run reveals only that run\'s per-model rows', () => {
    const fixture = setup(SUMMARY);
    fixture.componentInstance.toggleRun(2);
    fixture.detectChanges();

    const currentRun = root(fixture).querySelector(
      '[data-testid="pipeline-token-usage-run"][data-current="true"]',
    ) as HTMLElement;
    expect(currentRun.querySelectorAll('[data-testid="pipeline-token-usage-run-model"]').length).toBe(2);
    // The other (collapsed) run still shows no model rows.
    expect(all(root(fixture), 'pipeline-token-usage-run-model').length).toBe(2);
  });

  it('toggling the total button from the DOM expands and collapses the breakdown', () => {
    const fixture = setup(SUMMARY);
    const btn = one(root(fixture), 'pipeline-token-usage-total-toggle') as HTMLButtonElement;
    btn.click();
    fixture.detectChanges();
    expect(all(root(fixture), 'pipeline-token-usage-total-model').length).toBe(2);
    btn.click();
    fixture.detectChanges();
    expect(all(root(fixture), 'pipeline-token-usage-total-model').length).toBe(0);
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
    // A fully unpriced lifetime aggregate is explicit and never renders $0.00.
    const totalCost = one(root(fixture), 'pipeline-token-usage-grand-total-cost')?.textContent ?? '';
    expect(totalCost).toContain('Unknown');
    expect(totalCost).not.toContain('$0.00');

    fixture.componentInstance.toggleSummary();
    fixture.detectChanges();
    const modelRow = one(root(fixture), 'pipeline-token-usage-total-model');
    expect(modelRow?.textContent).toContain('n/a');
  });

  it('marks a mixed priced and unpriced lifetime aggregate as partial', () => {
    const summary: PipelineModelUsageSummary = {
      runs: [run(1, true, [
        model('claude-haiku-4-5', 700, 0.25),
        model('unpriced-test-model', 300, 0, false),
      ])],
      totalByModel: [
        model('claude-haiku-4-5', 700, 0.25),
        model('unpriced-test-model', 300, 0, false),
      ],
      totalTokens: 1_000,
      totalCostUsd: 0.25,
      anyModelUnknown: true,
    };

    const fixture = setup(summary);
    expect(one(root(fixture), 'pipeline-token-usage-grand-total-cost')?.textContent)
      .toContain('$0.25 partial');
  });
});
