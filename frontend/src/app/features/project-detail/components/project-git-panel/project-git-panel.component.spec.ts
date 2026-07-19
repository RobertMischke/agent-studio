import { beforeAll, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectGitPanelComponent } from './project-git-panel.component';
import { loadDiff2Html } from '../../../../utils/diff2html-lazy';
import type { GitProjectInventory } from '../../../git';

// Warm the shared lazy diff2html module so <app-diff-content> renders
// synchronously and no dynamic import resolves after the environment teardown.
beforeAll(async () => {
  await loadDiff2Html();
});

function inventoryFixture(overrides: Partial<GitProjectInventory> = {}): GitProjectInventory {
  return {
    projectName: 'Demo',
    repositoryPath: 'C:/repo/demo',
    isRepo: true,
    currentBranch: 'main',
    worktrees: [
      { path: 'C:/repo/demo', branch: 'main', headSha: 'a'.repeat(40), headShortSha: 'aaaaaaa', isPrimary: true, isDetached: false, isBare: false },
      { path: 'C:/repo/demo-task-1', branch: 'task/1', headSha: 'b'.repeat(40), headShortSha: 'bbbbbbb', isPrimary: false, isDetached: false, isBare: false },
    ],
    branches: [
      { name: 'main', category: 'main', tipSha: 'a'.repeat(40), tipShortSha: 'aaaaaaa', isCurrent: true, upstream: 'origin/main', ahead: 0, behind: 0, lastCommitSubject: 'seed', lastCommitAtUtc: '2026-07-01T00:00:00Z', worktreePath: 'C:/repo/demo' },
      { name: 'task/1', category: 'task', tipSha: 'b'.repeat(40), tipShortSha: 'bbbbbbb', isCurrent: false, upstream: null, ahead: 2, behind: 0, lastCommitSubject: 'task work', lastCommitAtUtc: '2026-07-02T00:00:00Z', worktreePath: 'C:/repo/demo-task-1' },
    ],
    recentCommits: [
      { sha: 'c'.repeat(40), shortSha: 'ccccccc', authorDateUtc: '2026-07-02T10:00:00Z', author: 'dev', subject: 'feat: add thing', filesChanged: 1, added: 3, removed: 1 },
    ],
    error: null,
    ...overrides,
  };
}

function setup() {
  TestBed.configureTestingModule({
    imports: [ProjectGitPanelComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  const fixture = TestBed.createComponent(ProjectGitPanelComponent);
  fixture.componentRef.setInput('projectName', 'Demo');
  fixture.detectChanges();
  const httpCtrl = TestBed.inject(HttpTestingController);
  return { fixture, httpCtrl, root: fixture.nativeElement as HTMLElement };
}

describe('ProjectGitPanelComponent', () => {
  it('renders the branch/worktree/history tree and repository path', () => {
    const { fixture, httpCtrl, root } = setup();
    httpCtrl.expectOne(r => r.url === '/api/git/inventory' && r.params.get('project') === 'Demo').flush(inventoryFixture());
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="git-repo-path"]')?.textContent).toContain('C:/repo/demo');
    expect(root.querySelector('[data-testid="git-tree-group-worktrees"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="git-tree-group-integration"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="git-tree-group-task"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="git-tree-group-history"]')).toBeTruthy();
    expect(root.querySelectorAll('[data-testid="git-branch-row"]').length).toBe(2);
    httpCtrl.verify();
  });

  it('loads files and a diff when a commit is selected', () => {
    const { fixture, httpCtrl, root } = setup();
    httpCtrl.expectOne(r => r.url === '/api/git/inventory').flush(inventoryFixture());
    fixture.detectChanges();

    const commitRow = root.querySelector<HTMLButtonElement>('[data-testid="git-commit-row"]');
    expect(commitRow).toBeTruthy();
    commitRow!.click();
    fixture.detectChanges();

    const sha = 'c'.repeat(40);
    httpCtrl.expectOne(r => r.url === '/api/git/project-commit/files' && r.params.get('sha') === sha)
      .flush({ sha, files: [{ status: 'M', path: 'src/thing.ts', added: 3, removed: 1 }] });
    fixture.detectChanges();

    expect(root.querySelectorAll('[data-testid="git-file-row"]').length).toBe(1);

    httpCtrl.expectOne(r => r.url === '/api/git/project-commit/diff' && r.params.get('path') === 'src/thing.ts')
      .flush({ diff: 'diff --git a/src/thing.ts b/src/thing.ts\n+added\n', hasDiff: true, emptyReason: null });
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="git-detail-card"]')?.textContent).toContain('feat: add thing');
    expect(fixture.componentInstance.diffText()).toContain('+added');
    httpCtrl.verify();
  });

  it('shows the empty state when the project is not a git repository', () => {
    const { fixture, httpCtrl, root } = setup();
    httpCtrl.expectOne(r => r.url === '/api/git/inventory').flush(
      inventoryFixture({ isRepo: false, repositoryPath: 'C:/repo/demo', currentBranch: null, worktrees: [], branches: [], recentCommits: [], error: 'Not a git repository: C:/repo/demo' }),
    );
    fixture.detectChanges();

    const empty = root.querySelector('[data-testid="git-empty"]');
    expect(empty).toBeTruthy();
    expect(empty?.textContent).toContain('Not a git repository');
    expect(root.querySelector('[data-testid="git-tree"]')).toBeNull();
    httpCtrl.verify();
  });
});
