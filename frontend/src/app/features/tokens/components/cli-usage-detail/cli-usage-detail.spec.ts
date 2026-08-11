import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CliUsageDetailComponent } from './cli-usage-detail';

describe('CliUsageDetailComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [CliUsageDetailComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(CliUsageDetailComponent);
    fixture.detectChanges();
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('derives project rows: slugs the name, totals tokens, sorts busiest-first', async () => {
    await TestBed.configureTestingModule({
      imports: [CliUsageDetailComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(CliUsageDetailComponent);
    fixture.componentRef.setInput('tokens', {
      byProject: [
        {
          project: 'Agent Taskboard',
          orchestratorLlmCalls: 1,
          inputTokens: 10,
          outputTokens: 10,
          cacheReadTokens: 0,
          cacheCreationTokens: 0,
          estimatedApiCostUsd: 0.1,
        },
        {
          project: 'Software Studio!',
          orchestratorLlmCalls: 9,
          inputTokens: 100,
          outputTokens: 100,
          cacheReadTokens: 50,
          cacheCreationTokens: 50,
          estimatedApiCostUsd: 0.9,
        },
        {
          project: '',
          orchestratorLlmCalls: 0,
          inputTokens: 5,
          outputTokens: 0,
          cacheReadTokens: 0,
          cacheCreationTokens: 0,
          estimatedApiCostUsd: 0,
        },
      ],
    } as never);
    fixture.detectChanges();

    const rows = fixture.componentInstance.projectRows();
    // Blank-named project is dropped; busiest (300 tokens) sorts first.
    expect(rows.map((r) => r.project)).toEqual(['Software Studio!', 'Agent Taskboard']);
    expect(rows[0].slug).toBe('software-studio');
    expect(rows[0].totalTokens).toBe(300);
    expect(rows[1].slug).toBe('agent-taskboard');
    expect(rows[1].totalTokens).toBe(20);
  });

  it('emits openProjectSettings with the raw project name', async () => {
    await TestBed.configureTestingModule({
      imports: [CliUsageDetailComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(CliUsageDetailComponent);
    let emitted: string | null = null;
    fixture.componentInstance.openProjectSettings.subscribe((name) => (emitted = name));
    fixture.componentInstance.openProjectSettings.emit('Agent Taskboard');
    expect(emitted).toBe('Agent Taskboard');
  });

  it('derives the recorded range from the model telemetry used by the usage totals', async () => {
    await TestBed.configureTestingModule({
      imports: [CliUsageDetailComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(CliUsageDetailComponent);
    fixture.componentRef.setInput('tokens', {
      byModel: [{
        model: 'gpt-5.6-sol', calls: 2, inputTokens: 100, outputTokens: 10,
        cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 1,
        modelPriced: true, firstRecordedAt: '2026-07-11T07:30:00Z',
        lastRecordedAt: '2026-08-11T16:42:00Z',
      }],
      byProject: [],
    } as never);
    fixture.detectChanges();

    expect(fixture.componentInstance.recordedCoverage()?.label)
      .toContain('Since 11 Jul 2026 · as of 11 Aug 2026');
    expect(fixture.componentInstance.modelCoverageFor('codex')?.lastRecordedAt)
      .toBe('2026-08-11T16:42:00.000Z');
  });
});
