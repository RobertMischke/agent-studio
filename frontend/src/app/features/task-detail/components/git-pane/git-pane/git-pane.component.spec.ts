import { computed, signal } from '@angular/core';
import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { GitPaneComponent } from './git-pane.component';
import { GitPaneService } from '../../../services/git-pane.service';
import type { GitFileChange, TaskCommitDetail, TaskCommitInfo } from '../../../../git';

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
    at: '2026-06-08T10:05:00Z',
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

function makeGitPaneMock() {
  const selectedCommitSha = signal<string | null>(null);
  const commitChain = signal<TaskCommitInfo[]>(commits);
  const commitFiles = signal<GitFileChange[]>(files);
  const commitDetail = signal<TaskCommitDetail | null>(null);

  return {
    viewMode: signal<'commit' | 'worktree'>('commit'),
    commitChain,
    selectedCommitSha,
    selectedDiffPath: signal<string | null>('src/one.ts'),
    diffText: signal(''),
    loading: signal(false),
    status: signal(null),
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
});
