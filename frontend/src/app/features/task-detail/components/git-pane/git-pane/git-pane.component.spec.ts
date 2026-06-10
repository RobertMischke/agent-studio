import { computed, signal } from '@angular/core';
import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { GitPaneComponent } from './git-pane.component';
import { GitPaneService } from '../../../services/git-pane.service';
import type { GitFileChange, GitStatus, TaskCommitDetail, TaskCommitInfo, TaskProvenanceView } from '../../../../git';
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

function makeGitPaneMock(options?: { viewMode?: 'commit' | 'worktree'; status?: GitStatus | null }) {
  const selectedCommitSha = signal<string | null>(null);
  const commitChain = signal<TaskCommitInfo[]>(commits);
  const commitFiles = signal<GitFileChange[]>(files);
  const commitDetail = signal<TaskCommitDetail | null>(null);

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
});
