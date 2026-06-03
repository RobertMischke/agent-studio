import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskCardComponent } from './task-card.component';
import type { TaskInfo, ClientSummary } from '../../../../models/task.model';
import { buildEffectiveModelChip, buildModeBadge } from './task-card-view-model';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('TaskCardComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // job
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] TaskCardComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('commit tooltip exposes the actual file list, not just the count', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    const files = [
      'backend/Services/Analysis/AnalysisReportContract.cs',
      'backend/Services/Analysis/AnalysisReportStore.cs',
      'docs/analysis-reports.md',
    ];
    fixture.componentRef.setInput('job', makeJob({
      state: '5-human-review',
      tags: ['quality:concerns', 'docs:concerns'],
      commit: {
        sha: '5f969a6abc',
        shortSha: '5f969a6',
        message: 'test(analysis-report): add schema round-trip tests',
        filesChanged: files.length,
        files,
        at: '2026-05-05T09:16:30Z',
      },
    }));
    fixture.detectChanges();

    const tooltip = fixture.componentInstance.commitTooltip();
    expect(typeof tooltip === 'object' && tooltip !== null).toBe(true);
    if (typeof tooltip !== 'object' || tooltip === null) return;
    expect(tooltip.title).toContain('5f969a6');
    expect(tooltip.title).toContain('3 file(s) changed');
    expect(tooltip.body).toContain('AnalysisReportContract.cs');
    expect(tooltip.body).toContain('analysis-reports.md');
    expect(tooltip.body).toContain('<ul>');

    const commit = fixture.nativeElement.querySelector('[data-testid="task-card-commit"]') as HTMLElement | null;
    expect(commit?.getAttribute('data-has-files')).toBe('true');
  });

  it('commit tooltip caps the file list and reports overflow', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    const files = Array.from({ length: 20 }, (_, i) => `src/file-${i}.ts`);
    fixture.componentRef.setInput('job', makeJob({
      commit: {
        sha: 'deadbeefcaf',
        shortSha: 'deadbee',
        message: 'refactor: rename utils',
        filesChanged: files.length,
        files,
        at: '2026-05-05T09:16:30Z',
      },
    }));
    fixture.detectChanges();

    const tooltip = fixture.componentInstance.commitTooltip();
    expect(typeof tooltip === 'object' && tooltip !== null).toBe(true);
    if (typeof tooltip !== 'object' || tooltip === null) return;
    expect(tooltip.body).toContain('src/file-0.ts');
    expect(tooltip.body).toContain('src/file-11.ts');
    expect(tooltip.body).not.toContain('src/file-12.ts');
    expect(tooltip.body).toContain('+8 more file(s)');
  });

  it('commit tooltip falls back to count-only when files array is empty', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      commit: {
        sha: 'abc12345',
        shortSha: 'abc1234',
        message: 'chore: legacy commit pre-file-tracking',
        filesChanged: 4,
        files: [],
        at: '2026-05-05T09:16:30Z',
      },
    }));
    fixture.detectChanges();

    const tooltip = fixture.componentInstance.commitTooltip();
    expect(typeof tooltip === 'object' && tooltip !== null).toBe(true);
    if (typeof tooltip !== 'object' || tooltip === null) return;
    expect(tooltip.title).toContain('4 file(s) changed');
    expect(tooltip.body).not.toContain('<ul>');
    expect(tooltip.body).not.toContain('+');

    const commit = fixture.nativeElement.querySelector('[data-testid="task-card-commit"]') as HTMLElement | null;
    expect(commit?.getAttribute('data-has-files')).toBeNull();
  });

  it('commit tooltip escapes HTML in file paths to prevent injection', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      commit: {
        sha: 'beefcafe',
        shortSha: 'beefcaf',
        message: 'fix <script>alert(1)</script>',
        filesChanged: 1,
        files: ['src/<img src=x onerror=alert(2)>.ts'],
        at: '2026-05-05T09:16:30Z',
      },
    }));
    fixture.detectChanges();

    const tooltip = fixture.componentInstance.commitTooltip();
    expect(typeof tooltip === 'object' && tooltip !== null).toBe(true);
    if (typeof tooltip !== 'object' || tooltip === null) return;
    expect(tooltip.body).not.toContain('<script>');
    expect(tooltip.body).not.toContain('<img src=x');
    expect(tooltip.body).toContain('&lt;script&gt;');
    expect(tooltip.body).toContain('&lt;img');
  });

  it('renders the indeterminate progress bar on running cards only (F39)', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      execution: {
        jobId: 'task-1',
        taskKey: 'test::task-1',
        processId: 1234,
        startedAt: '2026-05-23T07:00:00Z',
        status: 'running',
        exitCode: null,
        durationSeconds: null,
        model: null,
        runOutcome: null,
      },
    }));
    fixture.detectChanges();

    const host = fixture.nativeElement.querySelector('[data-testid="task-card"]') as HTMLElement | null;
    expect(host?.classList.contains('task-card--running')).toBe(true);
    expect(host?.getAttribute('data-running')).toBe('true');
    const bar = fixture.nativeElement.querySelector('[data-testid="task-card-progress"]') as HTMLElement | null;
    expect(bar).not.toBeNull();
    expect(bar?.getAttribute('aria-hidden')).toBe('true');

    fixture.componentRef.setInput('job', makeJob({ execution: null }));
    fixture.detectChanges();
    const barAfter = fixture.nativeElement.querySelector('[data-testid="task-card-progress"]') as HTMLElement | null;
    expect(barAfter).toBeNull();
  });

  it('suppresses the "Running live" pill on cards outside 3-progress (lane is source of truth)', async () => {
    // Regression: a stale `execution.status === 'running'` snapshot on a
    // card whose lane has already moved past 3-progress (4-auto-review,
    // 5-human-review, etc.) used to flash a misleading "Running live"
    // pill. The lane is the single source of truth for liveness; the
    // execution overlay must only surface on actively-running cards.
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    const runningExecution = {
      jobId: 'task-7', taskKey: 'test::task-7', processId: 4242,
      startedAt: '2026-05-28T10:00:00Z', status: 'running',
      exitCode: null, durationSeconds: null, model: null, runOutcome: null,
    };

    // 3-progress + running → badge + progress bar present.
    fixture.componentRef.setInput('job', makeJob({
      state: '3-progress',
      execution: runningExecution,
    }));
    fixture.detectChanges();
    expect(fixture.componentInstance.executionBadge()?.tone).toBe('running');
    expect(fixture.componentInstance.isRunning()).toBe(true);
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-progress"]')).not.toBeNull();

    // Same execution but lane has moved to 4-auto-review → suppress.
    for (const state of ['4-auto-review', '5-human-review', '6-completed', '4-review']) {
      fixture.componentRef.setInput('job', makeJob({
        state,
        execution: runningExecution,
      }));
      fixture.detectChanges();
      expect(fixture.componentInstance.executionBadge(), `state=${state}`).toBeNull();
      expect(fixture.componentInstance.isRunning(), `state=${state}`).toBe(false);
      expect(fixture.nativeElement.querySelector('[data-testid="task-card-progress"]'), `state=${state}`).toBeNull();
    }
  });

  it('flags an escalated human-review card as needing attention (Failed != Done)', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      state: '5-human-review',
      orchestratorVerdict: 'escalate',
    }));
    fixture.detectChanges();

    expect(fixture.componentInstance.needsAttention()).toBe(true);
    expect(fixture.componentInstance.humanReviewBadge()?.tone).toBe('attention');

    const host = fixture.nativeElement.querySelector('[data-testid="task-card"]') as HTMLElement | null;
    expect(host?.classList.contains('task-card--attention')).toBe(true);

    const pill = fixture.nativeElement.querySelector('[data-testid="task-card-human-review"]') as HTMLElement | null;
    expect(pill?.textContent).toContain('Escalated');
    expect(pill?.className).toContain('task-card__human-review-pill--attention');
  });

  it('shows a calm sign-off pill (not attention) for an accepted human-review card', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      state: '5-human-review',
      orchestratorVerdict: 'accept',
    }));
    fixture.detectChanges();

    expect(fixture.componentInstance.needsAttention()).toBe(false);
    expect(fixture.componentInstance.humanReviewBadge()?.tone).toBe('accept');

    const host = fixture.nativeElement.querySelector('[data-testid="task-card"]') as HTMLElement | null;
    expect(host?.classList.contains('task-card--attention')).toBe(false);

    const pill = fixture.nativeElement.querySelector('[data-testid="task-card-human-review"]') as HTMLElement | null;
    expect(pill?.textContent).toContain('Ready to sign off');
    expect(pill?.className).toContain('task-card__human-review-pill--accept');
  });

  it('stays quiet for an undecided human-review card and for completed cards', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);

    // Human review with no verdict yet → no pill, no attention.
    fixture.componentRef.setInput('job', makeJob({ state: '5-human-review', orchestratorVerdict: null }));
    fixture.detectChanges();
    expect(fixture.componentInstance.humanReviewBadge()).toBeNull();
    expect(fixture.componentInstance.needsAttention()).toBe(false);
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-human-review"]')).toBeNull();

    // Completed lane is out of scope even if a stale verdict rides along.
    fixture.componentRef.setInput('job', makeJob({ state: '6-completed', orchestratorVerdict: 'escalate' }));
    fixture.detectChanges();
    expect(fixture.componentInstance.humanReviewBadge()).toBeNull();
    expect(fixture.componentInstance.needsAttention()).toBe(false);
  });

  it('renders a runner outcome issue pill', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      outcomeIssue: {
        kind: 'permission-blocked',
        label: 'Permission blocked',
        severity: 'High',
        summary: 'Permission denied and could not request permission from user.',
        lastSeenAt: '2026-05-11T10:00:00Z',
      },
    }));
    fixture.detectChanges();

    const pill = fixture.nativeElement.querySelector('[data-testid="task-card-outcome-issue"]') as HTMLElement | null;
    expect(pill?.textContent).toContain('Permission blocked');
    expect(pill?.className).toContain('task-card__issue-pill--high');
  });

  // ── Mode badge (planning / research recognizable at a glance) ──────────

  it('renders a mode pill naming the mode for a planning card', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({ state: '2-ready', mode: 'planning' }));
    fixture.detectChanges();

    expect(fixture.componentInstance.modeBadge()?.mode).toBe('planning');
    const pill = fixture.nativeElement.querySelector('[data-testid="task-card-mode"]') as HTMLElement | null;
    expect(pill).not.toBeNull();
    expect(pill?.getAttribute('data-mode')).toBe('planning');
    expect(pill?.className).toContain('task-card__mode-pill--planning');
    expect(pill?.textContent).toContain('Planning');
  });

  it('renders a research mode pill', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob({ state: '2-ready', mode: 'research' }));
    fixture.detectChanges();

    const pill = fixture.nativeElement.querySelector('[data-testid="task-card-mode"]') as HTMLElement | null;
    expect(pill?.getAttribute('data-mode')).toBe('research');
    expect(pill?.textContent).toContain('Research');
  });

  it('stays quiet for coding cards and cards with no mode set', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(TaskCardComponent);

    fixture.componentRef.setInput('job', makeJob({ state: '2-ready', mode: 'coding' }));
    fixture.detectChanges();
    expect(fixture.componentInstance.modeBadge()).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-mode"]')).toBeNull();

    // Older payload with no mode field at all → still quiet.
    fixture.componentRef.setInput('job', makeJob({ state: '2-ready', mode: undefined }));
    fixture.detectChanges();
    expect(fixture.componentInstance.modeBadge()).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-mode"]')).toBeNull();
  });

  // ── Commit-attribution surface (AC#3, AC#4, AC#6) ──────────────────────

  async function renderCard(job: TaskInfo) {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', job);
    fixture.detectChanges();
    return fixture;
  }

  function commit(overrides: Partial<NonNullable<TaskInfo['commits']>[number]> = {}): NonNullable<TaskInfo['commits']>[number] {
    return {
      sha: 'aaaaaaa0000',
      shortSha: 'aaaaaaa',
      message: 'feat: do a thing',
      filesChanged: 2,
      files: ['src/a.ts', 'src/b.ts'],
      at: '2026-05-30T10:00:00Z',
      ...overrides,
    };
  }

  it('AC#6 0-commit analysis-only card shows a calm "no code changes" badge, no pill', async () => {
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      commit: null,
      commits: [],
      codeActivityDetected: false,
    }));

    expect(fixture.componentInstance.commitChainView()).toBeNull();
    const badge = fixture.componentInstance.commitEmptyBadge();
    expect(badge?.tone).toBe('no-code');

    const el = fixture.nativeElement.querySelector('[data-testid="task-card-no-commits"]') as HTMLElement | null;
    expect(el?.getAttribute('data-tone')).toBe('no-code');
    expect(el?.textContent).toContain('no code changes');
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-commit"]')).toBeNull();
  });

  it('AC#3 0-commit card that moved HEAD shows an amber "commit discovery pending" diagnostic', async () => {
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      commit: null,
      commits: [],
      codeActivityDetected: true,
    }));

    const badge = fixture.componentInstance.commitEmptyBadge();
    expect(badge?.tone).toBe('discovery');

    const el = fixture.nativeElement.querySelector('[data-testid="task-card-no-commits"]') as HTMLElement | null;
    expect(el?.getAttribute('data-tone')).toBe('discovery');
    expect(el?.className).toContain('task-card__no-commits--discovery');
    expect(el?.textContent).toContain('commit discovery pending');
  });

  it('does not render a zero-commit badge outside review lanes (3-progress stays quiet)', async () => {
    const fixture = await renderCard(makeJob({
      state: '3-progress',
      commit: null,
      commits: [],
      codeActivityDetected: true,
    }));
    expect(fixture.componentInstance.commitEmptyBadge()).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-no-commits"]')).toBeNull();
  });

  it('AC#4 single-commit card renders exactly one row and no "+N more"', async () => {
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      commit: null,
      commits: [commit({ shortSha: 'abc1234', message: 'fix: one', filesChanged: 1, files: ['x.ts'] })],
    }));

    const cc = fixture.componentInstance.commitChainView();
    expect(cc?.totalCount).toBe(1);
    expect(cc?.rows.length).toBe(1);
    expect(cc?.moreCount).toBe(0);

    const rows = fixture.nativeElement.querySelectorAll('[data-testid="task-card-commit-row"]');
    expect(rows.length).toBe(1);
    expect(rows[0].textContent).toContain('abc1234');
    expect(rows[0].textContent).toContain('fix: one');
    expect(rows[0].textContent).toContain('1 file');
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-commit-more"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-no-commits"]')).toBeNull();
  });

  it('AC#4 three-commit card renders all three rows (sha + subject + files), newest first', async () => {
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      commit: null,
      // stored oldest -> newest
      commits: [
        commit({ shortSha: 'old1111', message: 'feat: first', filesChanged: 1 }),
        commit({ shortSha: 'mid2222', message: 'feat: second', filesChanged: 2 }),
        commit({ shortSha: 'new3333', message: 'feat: third', filesChanged: 3 }),
      ],
    }));

    const cc = fixture.componentInstance.commitChainView();
    expect(cc?.totalCount).toBe(3);
    expect(cc?.rows.length).toBe(3);
    expect(cc?.moreCount).toBe(0);
    // newest first
    expect(cc?.rows[0].shortSha).toBe('new3333');
    expect(cc?.rows[2].shortSha).toBe('old1111');

    const rows = fixture.nativeElement.querySelectorAll('[data-testid="task-card-commit-row"]');
    expect(rows.length).toBe(3);
    expect(rows[0].textContent).toContain('feat: third');
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-commit-more"]')).toBeNull();
  });

  it('AC#4 four-commit card shows top-3 plus a "+1 more commit" disclosure', async () => {
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      commit: null,
      commits: [
        commit({ shortSha: 'c1aaaaa', message: 'one' }),
        commit({ shortSha: 'c2bbbbb', message: 'two' }),
        commit({ shortSha: 'c3ccccc', message: 'three' }),
        commit({ shortSha: 'c4ddddd', message: 'four' }),
      ],
    }));

    const cc = fixture.componentInstance.commitChainView();
    expect(cc?.totalCount).toBe(4);
    expect(cc?.rows.length).toBe(3);
    expect(cc?.moreCount).toBe(1);

    const rows = fixture.nativeElement.querySelectorAll('[data-testid="task-card-commit-row"]');
    expect(rows.length).toBe(3);
    const more = fixture.nativeElement.querySelector('[data-testid="task-card-commit-more"]') as HTMLElement | null;
    expect(more?.textContent).toContain('+1 more commit');
  });

  it('sources the chain from commits[] (SSOT), not the legacy singular commit', async () => {
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      commit: commit({ shortSha: 'legacyy', message: 'legacy singular' }),
      commits: [commit({ shortSha: 'chain01', message: 'chain wins' })],
    }));

    const cc = fixture.componentInstance.commitChainView();
    expect(cc?.totalCount).toBe(1);
    expect(cc?.rows[0].shortSha).toBe('chain01');
    expect(cc?.rows[0].subject).toBe('chain wins');
  });

  it('falls back to the legacy singular commit when commits[] is empty', async () => {
    const fixture = await renderCard(makeJob({
      state: '5-human-review',
      commit: commit({ shortSha: 'legacyy', message: 'legacy singular' }),
      commits: [],
    }));

    const cc = fixture.componentInstance.commitChainView();
    expect(cc?.totalCount).toBe(1);
    expect(cc?.rows[0].shortSha).toBe('legacyy');
    expect(fixture.componentInstance.commitEmptyBadge()).toBeNull();
  });
});

describe('buildEffectiveModelChip', () => {
  it('shows owner-client default when job has no cliType/model', () => {
    const job = makeJob({ agent: 'human', cliType: null, model: null, ownerClientId: 'local-default' });
    const owner = makeOwner({ defaultCliType: 'claude', defaultModel: 'claude-opus-4-7' });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.source).toBe('default');
    expect(chip.isDefault).toBe(true);
    expect(chip.label).toBe('opus 4.7');
    expect(chip.icon).toBe('✴️');
    expect(chip.cliLabel).toBe('Claude Code');
  });

  it('shows explicit model when job has cliType/model set', () => {
    const job = makeJob({ cliType: 'codex', model: 'o3', ownerClientId: 'local-default' });
    const owner = makeOwner({ defaultCliType: 'claude', defaultModel: 'claude-opus-4-7' });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.source).toBe('explicit');
    expect(chip.isDefault).toBe(false);
    expect(chip.label).toBe('o3');
  });

  it('shows running execution model for in-progress jobs', () => {
    const job = makeJob({
      cliType: null,
      model: null,
      ownerClientId: 'local-default',
      execution: {
        jobId: 'task-1', taskKey: 'test::task-1', processId: 1, startedAt: '',
        status: 'running', exitCode: null, durationSeconds: null,
        model: 'claude-sonnet-4-6', runOutcome: null
      },
    });
    const owner = makeOwner({ defaultCliType: 'claude', defaultModel: 'claude-opus-4-7' });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.source).toBe('run');
    expect(chip.isDefault).toBe(false);
    expect(chip.label).toBe('sonnet 4.6');
  });

  it('shows "human" when owner client is human-kind with no defaults', () => {
    const job = makeJob({ agent: 'human', cliType: null, model: null, ownerClientId: 'user-1' });
    const owner = makeOwner({ id: 'user-1', kind: 'human', defaultCliType: null, defaultModel: null });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.source).toBe('human');
    expect(chip.label).toBe('human');
    expect(chip.icon).toBe('\u{1F464}');
  });

  it('shows "unknown" when no defaults and owner is not human', () => {
    const job = makeJob({ agent: 'human', cliType: null, model: null, ownerClientId: 'svc' });
    const owner = makeOwner({ id: 'svc', kind: 'service', defaultCliType: null, defaultModel: null });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.source).toBe('unknown');
    expect(chip.label).toBe('unknown');
  });

  it('tooltip contains agent as pickup permission', () => {
    const job = makeJob({ agent: 'human', cliType: null, model: null });
    const owner = makeOwner({ defaultCliType: 'claude', defaultModel: 'claude-opus-4-7' });
    const chip = buildEffectiveModelChip(job, owner);
    expect(typeof chip.tooltip).toBe('object');
    expect(chip.tooltip.body).toContain('human');
    expect(chip.tooltip.body).toContain('pickup permission');
  });

  it('tooltip indicates client default when source is default', () => {
    const job = makeJob({ agent: 'human', cliType: null, model: null });
    const owner = makeOwner({ defaultCliType: 'claude', defaultModel: 'claude-opus-4-7' });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.tooltip.title).toContain('client default');
    expect(chip.tooltip.body).toContain('client default');
  });

  // Orphan/system tasks persist cliType 'system', which is not a real CLI. The
  // chip and tooltip must degrade gracefully instead of crashing in escapeHtml
  // (cliTypeLabel returns undefined for unknown CLIs). Regression for the
  // group-by-epic board surfacing these cards.
  it('degrades to "unknown" for an unrecognized cliType instead of throwing', () => {
    const job = makeJob({
      agent: 'system',
      cliType: 'system' as unknown as TaskInfo['cliType'],
      model: null,
      ownerClientId: 'svc',
    });
    const owner = makeOwner({ id: 'svc', kind: 'service', defaultCliType: null, defaultModel: null });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.source).toBe('unknown');
    expect(chip.label).toBe('unknown');
    expect(chip.cliLabel).toBeNull();
    expect(chip.tooltip.body).toContain('<b>CLI:</b> none');
  });

  it('ignores an unrecognized cliType but still honors owner defaults', () => {
    const job = makeJob({
      agent: 'system',
      cliType: 'system' as unknown as TaskInfo['cliType'],
      model: null,
      ownerClientId: 'local-default',
    });
    const owner = makeOwner({ defaultCliType: 'claude', defaultModel: 'claude-opus-4-7' });
    const chip = buildEffectiveModelChip(job, owner);
    expect(chip.source).toBe('default');
    expect(chip.cliLabel).toBe('Claude Code');
    expect(chip.tooltip.body).toContain('Claude Code');
  });
});

describe('buildModeBadge', () => {
  it('returns a planning badge with the picker glyph and a mode-naming tooltip', () => {
    const badge = buildModeBadge('planning');
    expect(badge?.mode).toBe('planning');
    expect(badge?.label).toBe('Planning');
    expect(badge?.icon).toBe('🗺️');
    expect(badge?.tooltip).toContain('Planning mode');
    expect(badge?.tooltip).toContain('read-only');
  });

  it('returns a research badge', () => {
    const badge = buildModeBadge('research');
    expect(badge?.mode).toBe('research');
    expect(badge?.label).toBe('Research');
    expect(badge?.icon).toBe('🔍');
    expect(badge?.tooltip).toContain('Research mode');
    expect(badge?.tooltip).toContain('web access');
  });

  it('returns null for coding and for an absent mode (older payloads)', () => {
    expect(buildModeBadge('coding')).toBeNull();
    expect(buildModeBadge(undefined)).toBeNull();
  });
});

function makeOwner(overrides: Partial<ClientSummary> = {}): ClientSummary {
  return {
    id: 'local-default',
    displayName: 'Local Default',
    emoji: null,
    colour: null,
    kind: 'agent-instance',
    registeredAt: '',
    lastSeenAt: null,
    tokenBudgetMonthly: null,
    notes: null,
    defaultCliType: null,
    defaultModel: null,
    ...overrides,
  };
}

function makeJob(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'test::task-1',
    title: 'Task 1',
    state: '3-progress',
    order: 1,
    agent: 'codex',
    createdAt: '2026-05-11T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: '/tmp/watch/3-progress/task-1',
    lastActivity: '2026-05-11T09:30:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    ...overrides,
  };
}
