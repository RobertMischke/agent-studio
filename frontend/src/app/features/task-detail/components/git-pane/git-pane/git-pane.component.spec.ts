import { computed, signal } from '@angular/core';
import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { GitPaneComponent } from './git-pane.component';
import { GitPaneService } from '../../../services/git-pane.service';
import { LayoutPanesService } from '../../../services/layout-panes.service';
import type { GitFileChange, GitStatus, TaskCommitDetail, TaskCommitInfo, TaskProvenanceView } from '../../../../git';
import type { CodeReviewListEntry } from '../../../../../services/task.service';
import { LARGE_DIFF_LINE_THRESHOLD } from '../../../../../utils/large-diff-gate';
import { formatCompactDateTime, formatDateTime, formatTime } from '../../../../../services/format.util';

const commits: TaskCommitInfo[] = [
  {
    sha: '1111111111111111111111111111111111111111',
    shortSha: '1111111',
    message: 'First commit',
    filesChanged: 1,
    files: ['src/one.ts'],
    at: '2026-06-08T10:00:00Z',
    attribution: 'automatic',
    confidence: 0.9,
  },
  {
    sha: '2222222222222222222222222222222222222222',
    shortSha: '2222222',
    message: 'Second commit',
    filesChanged: 1,
    files: ['src/two.ts'],
    at: '2026-06-09T10:00:00Z',
    attribution: 'automatic',
    confidence: 0.95,
  },
];

const files: GitFileChange[] = [
  {
    path: 'src/one.ts',
    status: 'M',
    added: 3,
    removed: 1,
  },
];

function worktreeStatus(isWorktree: boolean): GitStatus {
  return {
    isRepo: true,
    branch: isWorktree ? 'task/demo-task' : 'develop',
    filesChanged: 0,
    totalAdded: 0,
    totalRemoved: 0,
    files: [],
    error: null,
    isWorktree,
  };
}

function makeGitPaneMock(options?: {
  viewMode?: 'commit' | 'worktree';
  status?: GitStatus | null;
  codeReviews?: CodeReviewListEntry[];
}) {
  const selectedCommitSha = signal<string | null>(null);
  const commitChain = signal<TaskCommitInfo[]>(commits);
  const commitFiles = signal<GitFileChange[]>(files);
  const commitDetail = signal<TaskCommitDetail | null>(null);
  const codeReviews = signal<CodeReviewListEntry[]>(options?.codeReviews ?? []);

  return {
    viewMode: signal<'commit' | 'worktree'>(options?.viewMode ?? 'commit'),
    commitChain,
    selectedCommitSha,
    selectedDiffPath: signal<string | null>('src/one.ts'),
    diffText: signal(''),
    loading: signal(false),
    status: signal<GitStatus | null>(options?.status ?? null),
    provenance: signal<TaskProvenanceView | null>(null),
    commitFiles,
    commitDetail,
    codeReviews,
    // Mirrors the real service computed: the newest review whose reviewed
    // commit matches the shown commit's SHA (prefix-tolerant), else null.
    commitReview: computed<CodeReviewListEntry | null>(() => {
      const sha = commitDetail()?.commit?.sha ?? null;
      if (!sha) return null;
      return (
        codeReviews().find(
          (r) => !!r.commit && (sha === r.commit || sha.startsWith(r.commit) || r.commit.startsWith(sha)),
        ) ?? null
      );
    }),
    isAggregate: computed(() => commitChain().length > 1 && selectedCommitSha() === null),
    selectAllCommits: () => selectedCommitSha.set(null),
    selectChainCommit: (sha: string) => {
      const entry = commitChain().find(c => c.sha === sha) ?? null;
      selectedCommitSha.set(sha);
      commitDetail.set(entry ? { commit: entry, files } : null);
    },
    selectDiffPath: () => undefined,
    refresh: () => undefined,
  };
}

function makeReview(overrides?: Partial<CodeReviewListEntry>): CodeReviewListEntry {
  return {
    fileName: 'code-review-2026-06-09.md',
    verdict: 'pass',
    summary: 'No blocking issues found.',
    model: 'claude-opus-4-8',
    cliType: 'claude',
    commit: commits[1].sha,
    runAt: '2026-06-09T11:00:00Z',
    ...overrides,
  };
}

describe('GitPaneComponent', () => {
  beforeEach(() => {
    localStorage.removeItem('taskboard.gitPane.commitGroupCollapsed');
  });

  it('renders the multi-commit group collapsed by default without a duplicate aggregate header', async () => {
    const git = makeGitPaneMock();
    await TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const toggle = root.querySelector<HTMLElement>('[data-testid="git-commit-group-toggle"]');
    expect(toggle?.getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('[data-testid="git-commit-chain"]')).toBeNull();
    expect(root.querySelector('[data-testid="git-commit-aggregate-header"]')).toBeNull();
    expect(root.textContent).toContain('All 2 commits');
  });

  it('shows one aggregate selector when the commit group is expanded', async () => {
    const git = makeGitPaneMock();
    await TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('[data-testid="git-commit-group-toggle"]')?.click();
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="git-commit-chain"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="git-commit-chain-all"]')?.textContent).toContain('All 2 commits');
    expect(root.querySelector('[data-testid="git-commit-aggregate-header"]')).toBeNull();
  });

  it('shows compact date and time so commit rows from different days are distinguishable', async () => {
    const git = makeGitPaneMock();
    await TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('[data-testid="git-commit-group-toggle"]')?.click();
    fixture.detectChanges();

    const meta = root.querySelectorAll<HTMLElement>('[data-testid="git-commit-chain-meta"]');
    const firstTimeOnly = formatTime(commits[0].at);
    const secondTimeOnly = formatTime(commits[1].at);
    expect(meta).toHaveLength(2);
    expect(meta[0].textContent?.trim()).toBe(`1f · ${formatCompactDateTime(commits[0].at)}`);
    expect(meta[1].textContent?.trim()).toBe(`1f · ${formatCompactDateTime(commits[1].at)}`);
    expect(firstTimeOnly).toBe(secondTimeOnly);
    expect(meta[0].textContent?.trim()).not.toBe(`1f · ${firstTimeOnly}`);
    expect(meta[1].textContent?.trim()).not.toBe(`1f · ${secondTimeOnly}`);
    expect(meta[0].textContent).not.toBe(meta[1].textContent);
    expect(fixture.componentInstance.commitChainTooltip(commits[0], 0)).toContain(formatDateTime(commits[0].at));
  });

  it('gates a large selected diff until the operator reveals it', async () => {
    const git = makeGitPaneMock();
    git.diffText.set(Array.from({ length: LARGE_DIFF_LINE_THRESHOLD }, (_, i) => `+line ${i}`).join('\n'));
    await TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const gated = root.querySelector('[data-testid="git-diff-gated"]');
    expect(gated?.textContent).toContain('src/one.ts');
    expect(gated?.textContent).toContain('Show diff');
    expect(root.querySelector('[data-testid="git-diff"]')).toBeNull();

    root.querySelector<HTMLButtonElement>('[data-testid="git-diff-show"]')?.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.diffGated()).toBe(false);
    expect(root.querySelector('[data-testid="git-diff-gated"]')).toBeNull();
  });

  it('labels the run-location as the task worktree when the live status is worktree-scoped (ASS-1731)', async () => {
    const git = makeGitPaneMock({ viewMode: 'worktree', status: worktreeStatus(true) });
    await TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const location = root.querySelector('[data-testid="git-run-location"]');
    expect(location?.textContent?.trim()).toBe('(Worktree)');
    expect(location?.classList.contains('git-view__location--worktree')).toBe(true);
    expect(root.textContent).toContain('task/demo-task');
  });

  it('labels the run-location as the main checkout when the live status is not worktree-scoped (ASS-1731)', async () => {
    const git = makeGitPaneMock({ viewMode: 'worktree', status: worktreeStatus(false) });
    await TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const location = root.querySelector('[data-testid="git-run-location"]');
    expect(location?.textContent?.trim()).toBe('(Haupt-Checkout)');
    expect(location?.classList.contains('git-view__location--worktree')).toBe(false);
  });

  it('renders a code-review rating badge on the commit line and jumps to the review on click (AGT-1995)', async () => {
    const git = makeGitPaneMock({ codeReviews: [makeReview({ verdict: 'concerns', summary: 'Two nits worth a look.' })] });
    await TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    // Pin the view to a single commit so the commit line (and its badge) render.
    git.selectChainCommit(commits[1].sha);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const badge = root.querySelector<HTMLButtonElement>('[data-testid="git-commit-review-badge"]');
    expect(badge).not.toBeNull();
    expect(badge?.getAttribute('data-verdict')).toBe('concerns');
    expect(badge?.textContent).toContain('Concerns');

    // Clicking asks the shared layout service to reveal + focus the Code
    // Review tab of the prompt pane.
    const layout = TestBed.inject(LayoutPanesService);
    badge?.click();
    expect(layout.requestedPromptTab()).toBe('code-review');
    expect(layout.panesVisible().prompt).toBe(true);
  });

  it('shows no rating badge when no review matches the shown commit (AGT-1995)', async () => {
    const git = makeGitPaneMock({ codeReviews: [makeReview({ commit: 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeef' })] });
    await TestBed.configureTestingModule({
      imports: [GitPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: GitPaneService, useValue: git },
        LayoutPanesService,
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(GitPaneComponent);
    fixture.componentRef.setInput('isActiveJob', true);
    git.selectChainCommit(commits[1].sha);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="git-commit-review-badge"]')).toBeNull();
  });
});
