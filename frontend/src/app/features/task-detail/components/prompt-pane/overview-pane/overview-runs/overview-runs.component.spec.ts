import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { RunRecord } from '../../../../../run-timeline';
import { OverviewRunsComponent } from './overview-runs.component';

describe('OverviewRunsComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [OverviewRunsComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  it('uses the runner-resolved model and thinking level before legacy fallbacks', () => {
    const fixture = setup([
      run(1, {
        cli: 'codex',
        model: 'gpt-5.6-sol',
        thinkingLevel: 'xhigh',
        executionContext: executionContext('legacy-context-model'),
        tokenSummary: tokenSummary('legacy-token-model'),
      }),
    ]);

    expect(text(fixture, 'overview-run-engine')).toBe('Codex · gpt-5.6-sol · xhigh');
  });

  it('keeps execution-context and token-summary model fallbacks for legacy runs', () => {
    const fixture = setup([
      run(1, {
        cli: 'codex',
        executionContext: null,
        tokenSummary: tokenSummary('gpt-5.6-terra'),
      }),
      run(2, {
        cli: 'claude',
        executionContext: executionContext('claude-opus-4-8'),
        tokenSummary: tokenSummary('ignored-token-model'),
      }),
    ]);
    const rows = all(fixture, 'overview-run-row');

    expect(textIn(rows[0], 'overview-run-engine')).toBe('Claude Code · opus 4.8');
    expect(textIn(rows[1], 'overview-run-engine')).toBe('Codex · gpt-5.6-terra');
  });

  it('still identifies a CLI when no model source exists', () => {
    const fixture = setup([run(1, { cli: 'gemini', model: null })]);

    expect(text(fixture, 'overview-run-engine')).toBe('Gemini');
  });

  it('renders every run newest first and reconciles the visible count', () => {
    const fixture = setup([
      run(1, { model: 'gpt-5.6-luna' }),
      run(2, { model: 'gpt-5.6-terra' }),
      run(3, { model: 'gpt-5.6-sol' }),
    ]);
    const rows = all(fixture, 'overview-run-row');

    expect(text(fixture, 'overview-runs-count')).toBe('3 runs');
    expect(rows.map((row) => row.getAttribute('data-run-index'))).toEqual(['3', '2', '1']);
    expect(rows.map((row) => textIn(row, 'overview-run-engine'))).toEqual([
      'Codex · gpt-5.6-sol',
      'Codex · gpt-5.6-terra',
      'Codex · gpt-5.6-luna',
    ]);
  });
});

function setup(runs: RunRecord[]) {
  const fixture = TestBed.createComponent(OverviewRunsComponent);
  fixture.componentRef.setInput('runs', runs);
  fixture.detectChanges();
  return fixture;
}

function run(index: number, overrides: Partial<RunRecord> = {}): RunRecord {
  return {
    index,
    intent: 'start',
    startedAt: `2026-07-26T10:0${index}:00Z`,
    endedAt: `2026-07-26T10:0${index}:30Z`,
    status: 'completed',
    cli: 'codex',
    exitCode: 0,
    durationSeconds: 30,
    inputSessionId: null,
    capturedSessionId: null,
    resumed: false,
    reason: null,
    userFollowup: null,
    lineStart: null,
    lineEnd: null,
    headShaBefore: null,
    headShaAfter: null,
    contextRef: null,
    ...overrides,
  };
}

function executionContext(model: string) {
  return {
    cli: 'claude',
    model,
    permissionMode: null,
    cwd: null,
    capturedAt: '2026-07-26T10:00:00Z',
    source: 'init-frame',
    sources: [],
  };
}

function tokenSummary(lastModel: string) {
  return {
    calls: 1,
    inputTokens: 10,
    outputTokens: 10,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    totalTokens: 20,
    lastModel,
    lastUpdate: '2026-07-26T10:00:00Z',
    entries: [],
  };
}

function all(fixture: ReturnType<typeof setup>, testId: string): HTMLElement[] {
  return Array.from(
    fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`),
  ) as HTMLElement[];
}

function text(fixture: ReturnType<typeof setup>, testId: string): string {
  const element = fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  return element?.textContent?.trim() ?? '';
}

function textIn(element: HTMLElement, testId: string): string {
  return element.querySelector(`[data-testid="${testId}"]`)?.textContent?.trim() ?? '';
}
