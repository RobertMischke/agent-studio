import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ResultViewComponent } from './result-view.component';
import type { TaskDetail } from '../../../../../models/task.model';
import type { ProtocolVerdict } from '../protocol-verdict';

function verdict(overrides: Partial<ProtocolVerdict> = {}): ProtocolVerdict {
  return {
    kind: 'ok',
    emoji: '🟢',
    label: 'Success',
    detail: 'Last run completed successfully.',
    duration: '4 min',
    superseded: null,
    ...overrides,
  };
}

function detail(statusMarkdown: string, overrides: Record<string, unknown> = {}): TaskDetail {
  return {
    info: {
      id: 'job-1',
      watchPath: '/ws',
      title: 'Fix the broken thing',
      taskType: 'bug',
      mode: 'coding',
      tags: ['code-review:grade-a'],
      tokenSummary: { totalTokens: 12_000 },
      commits: [{}],
      codeActivityDetected: true,
      ...overrides,
    },
    statusMarkdown,
  } as unknown as TaskDetail;
}

// A status.md with only the header + overview, so the detail layer (and the
// heavier beautiful-results renderer) stays out of the DOM for the head/overview
// assertions.
const HEAD_ONLY = `# Status

- Result: Success
- Duration: 4 min

## Overview
- Problem: The card was unreadable.
- Solution: Re-toned the surface.
`;

async function build(d: TaskDetail, v: ProtocolVerdict) {
  await TestBed.configureTestingModule({
    imports: [ResultViewComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(ResultViewComponent);
  fixture.componentRef.setInput('detail', d);
  fixture.componentRef.setInput('verdict', v);
  fixture.detectChanges();
  return fixture;
}

describe('ResultViewComponent', () => {
  it('renders the case badge and the overview problem/solution', async () => {
    const fixture = await build(detail(HEAD_ONLY), verdict());
    const el = fixture.nativeElement as HTMLElement;

    const badge = el.querySelector('[data-testid="result-case-badge"]');
    expect(badge?.textContent).toContain('Bugfix');

    expect(el.querySelector('[data-testid="result-overview-problem"]')?.textContent).toContain('unreadable');
    expect(el.querySelector('[data-testid="result-overview-solution"]')?.textContent).toContain('Re-toned');
  });

  it('renders the metric head with verdict, grade, duration and tokens chips', async () => {
    const fixture = await build(detail(HEAD_ONLY), verdict());
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('[data-testid="result-metric-verdict"]')?.textContent).toContain('Success');
    expect(el.querySelector('[data-testid="result-metric-grade"]')?.textContent).toContain('Grade A');
    expect(el.querySelector('[data-testid="result-metric-duration"]')?.textContent).toContain('4 min');
    expect(el.querySelector('[data-testid="result-metric-tokens"]')).not.toBeNull();
  });

  it('marks a synthesized overview so the origin is honest', async () => {
    const legacy = `# Status\n\n- Result: Success\n\n## What Was Done\n- Did the thing.\n`;
    const fixture = await build(detail(legacy), verdict({ duration: null }));
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="result-overview-synthesized"]')).not.toBeNull();
  });

  it('renders the detail layer when there is body content beyond the overview', async () => {
    const full = `${HEAD_ONLY}\n## What Was Done\n- Edited a file.\n`;
    const fixture = await build(detail(full), verdict());
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="result-detail"]')).not.toBeNull();
  });

  it('reflects the blocked case tone when the run did not land', async () => {
    const fixture = await build(detail(HEAD_ONLY), verdict({ kind: 'problem', label: 'Blocked', emoji: '🔴' }));
    const el = fixture.nativeElement as HTMLElement;
    const article = el.querySelector('[data-testid="result-view"]');
    expect(article?.getAttribute('data-case')).toBe('blocked');
    expect(el.querySelector('[data-testid="result-case-badge"]')?.textContent).toContain('Blocked');
  });
});
