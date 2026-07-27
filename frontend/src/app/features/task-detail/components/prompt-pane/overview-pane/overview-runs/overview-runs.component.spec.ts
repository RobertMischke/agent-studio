import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { RunRecord } from '../../../../../run-timeline';
import { OverviewRunsComponent } from './overview-runs.component';

describe('OverviewRunsComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [OverviewRunsComponent],
      providers: [provideZonelessChangeDetection()],
    });
  });

  it('renders every card-scoped run with trigger, result, duration and a visible-row sum', () => {
    const fixture = setup([
      run(1, { intent: 'start', status: 'completed', durationSeconds: 12 }),
      run(2, {
        intent: 'continue',
        status: 'failed',
        durationSeconds: 65,
        userFollowup: 'Address the failed browser check.',
      }),
      run(3, { intent: 'recovery', status: 'running', durationSeconds: null }),
    ]);
    const rows = all(fixture, 'overview-run-row');

    expect(rows).toHaveLength(3);
    expect(one(fixture, 'overview-runs-count').textContent?.trim()).toBe('3 runs');
    expect(rows[0].getAttribute('data-run-index')).toBe('3');
    expect(testText(rows[0], 'overview-run-trigger')).toBe('Recovery');
    expect(testText(rows[0], 'overview-run-result')).toContain('Running');
    expect(testText(rows[0], 'overview-run-duration')).toBe('In progress');
    expect(testText(rows[1], 'overview-run-trigger')).toBe('User follow-up');
    expect(testText(rows[1], 'overview-run-result')).toContain('Failed');
    expect(testText(rows[1], 'overview-run-duration')).toBe('1m 5s');
    expect(testText(rows[2], 'overview-run-trigger')).toBe('Initial start');
  });

  it('shows optional per-run pipeline token usage only where it was recorded', () => {
    const fixture = setup([
      run(1),
      run(2, {
        tokenSummary: {
          calls: 2,
          inputTokens: 900,
          outputTokens: 400,
          cacheReadTokens: 0,
          cacheCreationTokens: 0,
          totalTokens: 1_300,
          lastModel: 'gpt-5.6',
          lastUpdate: '2026-07-26T10:01:00Z',
          entries: [],
        },
      }),
    ]);
    const rows = all(fixture, 'overview-run-row');

    expect(all(fixture, 'overview-run-tokens')).toHaveLength(1);
    expect(testText(rows[0], 'overview-run-tokens')).toBe('1.3k tokens');
    expect(rows[1].querySelector('[data-testid="overview-run-tokens"]')).toBeNull();
  });

  it('uses the persisted CORE duration only when timeline rows have no duration', () => {
    const fixture = setup([run(1, { status: 'interrupted', durationSeconds: null })], 125);

    expect(one(fixture, 'overview-runs-duration').textContent?.trim()).toBe('2m 5s total');
    expect(testText(all(fixture, 'overview-run-row')[0], 'overview-run-duration')).toBe(
      'Not recorded',
    );
  });

  it('renders the fallback total without inventing a run row or count badge', () => {
    const fixture = setup([], 75);

    expect(all(fixture, 'overview-run-row')).toHaveLength(0);
    expect(query(fixture, 'overview-runs-count')).toBeNull();
    expect(one(fixture, 'overview-runs-duration').textContent?.trim()).toBe('1m 15s total');
  });

  it('renders nothing before the current card has any run evidence', () => {
    const fixture = setup([]);

    expect(query(fixture, 'overview-runs')).toBeNull();
  });

  it('tracks a re-polled row by its durable run index', () => {
    const fixture = setup([run(1), run(2)]);
    const retained = all(fixture, 'overview-run-row')[0];

    fixture.componentRef.setInput('runs', [
      run(1),
      run(2, { status: 'failed' }),
      run(3, { status: 'running', durationSeconds: null }),
    ]);
    fixture.detectChanges();

    const rows = all(fixture, 'overview-run-row');
    expect(rows).toHaveLength(3);
    expect(rows[1]).toBe(retained);
    expect(testText(rows[1], 'overview-run-result')).toContain('Failed');
  });
});

function setup(runs: RunRecord[], fallbackDurationSeconds = 0) {
  const fixture = TestBed.createComponent(OverviewRunsComponent);
  fixture.componentRef.setInput('runs', runs);
  fixture.componentRef.setInput('fallbackDurationSeconds', fallbackDurationSeconds);
  fixture.detectChanges();
  return fixture;
}

function run(index: number, overrides: Partial<RunRecord> = {}): RunRecord {
  return {
    index,
    intent: 'continue',
    startedAt: `2026-07-26T10:0${index}:00Z`,
    endedAt: `2026-07-26T10:0${index}:10Z`,
    status: 'completed',
    cli: 'codex',
    exitCode: 0,
    durationSeconds: 10,
    inputSessionId: null,
    capturedSessionId: null,
    resumed: index > 1,
    reason: null,
    userFollowup: null,
    lineStart: index,
    lineEnd: index + 1,
    headShaBefore: null,
    headShaAfter: null,
    contextRef: null,
    ...overrides,
  };
}

function query(fixture: { nativeElement: HTMLElement }, testId: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
}

function one(fixture: { nativeElement: HTMLElement }, testId: string): HTMLElement {
  return query(fixture, testId) as HTMLElement;
}

function all(fixture: { nativeElement: HTMLElement }, testId: string): HTMLElement[] {
  return Array.from(
    fixture.nativeElement.querySelectorAll<HTMLElement>(`[data-testid="${testId}"]`),
  );
}

function testText(root: HTMLElement, testId: string): string {
  return root.querySelector<HTMLElement>(`[data-testid="${testId}"]`)?.textContent?.trim() ?? '';
}
