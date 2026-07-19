import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectGitCleanupComponent } from './project-git-cleanup.component';
import type { CleanupCandidate, GitCleanupPlan } from '../../../git';

function candidate(overrides: Partial<CleanupCandidate>): CleanupCandidate {
  return {
    kind: 'localBranch',
    name: 'task/1',
    remote: null,
    tipSha: 'b'.repeat(40),
    tipShortSha: 'bbbbbbb',
    mergeStatus: 'merged',
    eligible: true,
    reason: 'Merged into develop.',
    ...overrides,
  };
}

function planFixture(overrides: Partial<GitCleanupPlan> = {}): GitCleanupPlan {
  return {
    projectName: 'Demo',
    repositoryPath: 'C:/repo/demo',
    isRepo: true,
    integrationBranch: 'develop',
    candidates: [
      candidate({ name: 'task/merged', mergeStatus: 'merged', eligible: true }),
      candidate({ name: 'task/unmerged', mergeStatus: 'unmerged', eligible: false, reason: 'Not merged into develop; kept (AGT-1945 invariant).' }),
    ],
    error: null,
    ...overrides,
  };
}

function setup() {
  TestBed.configureTestingModule({
    imports: [ProjectGitCleanupComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  const fixture = TestBed.createComponent(ProjectGitCleanupComponent);
  fixture.componentRef.setInput('projectName', 'Demo');
  fixture.detectChanges();
  const httpCtrl = TestBed.inject(HttpTestingController);
  return { fixture, httpCtrl, root: fixture.nativeElement as HTMLElement };
}

function open(fixture: ReturnType<typeof setup>['fixture'], root: HTMLElement) {
  root.querySelector<HTMLButtonElement>('[data-testid="git-cleanup-toggle"]')!.click();
  fixture.detectChanges();
}

describe('ProjectGitCleanupComponent', () => {
  it('loads the plan on first open and pre-selects only eligible candidates', () => {
    const { fixture, httpCtrl, root } = setup();
    open(fixture, root);

    httpCtrl.expectOne(r => r.url === '/api/git/cleanup/plan' && r.params.get('project') === 'Demo').flush(planFixture());
    fixture.detectChanges();

    const rows = root.querySelectorAll('[data-testid="git-cleanup-candidate"]');
    expect(rows.length).toBe(2);

    const checks = root.querySelectorAll<HTMLInputElement>('[data-testid="git-cleanup-check"]');
    const merged = Array.from(checks).find(c => c.closest('[data-name="task/merged"]'))!;
    const unmerged = Array.from(checks).find(c => c.closest('[data-name="task/unmerged"]'))!;
    expect(merged.checked).toBe(true);
    expect(unmerged.checked).toBe(false);
    // The unmerged branch cannot be selected at all (AGT-1945 guardrail in the UI).
    expect(unmerged.disabled).toBe(true);

    // Delete button reflects the one pre-selected eligible item.
    expect(root.querySelector('[data-testid="git-cleanup-delete"]')?.textContent).toContain('(1)');
    httpCtrl.verify();
  });

  it('requires a confirm and posts only the eligible selection, then shows the report', () => {
    const { fixture, httpCtrl, root } = setup();
    open(fixture, root);
    httpCtrl.expectOne(r => r.url === '/api/git/cleanup/plan').flush(planFixture());
    fixture.detectChanges();

    // First click asks for confirmation; nothing is posted yet.
    root.querySelector<HTMLButtonElement>('[data-testid="git-cleanup-delete"]')!.click();
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="git-cleanup-confirm-bar"]')).toBeTruthy();

    root.querySelector<HTMLButtonElement>('[data-testid="git-cleanup-confirm"]')!.click();
    fixture.detectChanges();

    const exec = httpCtrl.expectOne(r => r.method === 'POST' && r.url === '/api/git/cleanup/execute');
    expect(exec.request.body.items).toEqual([{ kind: 'localBranch', name: 'task/merged', remote: null }]);
    exec.flush({
      projectName: 'Demo',
      integrationBranch: 'develop',
      isRepo: true,
      deletedCount: 1,
      keptCount: 0,
      actions: [{ kind: 'localBranch', name: 'task/merged', remote: null, deleted: true, reason: 'Deleted local branch (merged into develop).' }],
      error: null,
    });
    fixture.detectChanges();

    // Post-execute re-analyse.
    httpCtrl.expectOne(r => r.url === '/api/git/cleanup/plan').flush(planFixture({ candidates: [] }));
    fixture.detectChanges();

    const result = root.querySelector('[data-testid="git-cleanup-result"]');
    expect(result?.textContent).toContain('1 deleted');
    expect(root.querySelectorAll('[data-testid="git-cleanup-action"]').length).toBe(1);
    httpCtrl.verify();
  });

  it('shows the empty-repo error state', () => {
    const { fixture, httpCtrl, root } = setup();
    open(fixture, root);
    httpCtrl.expectOne(r => r.url === '/api/git/cleanup/plan').flush(
      planFixture({ isRepo: false, candidates: [], error: 'Project has no resolvable git repository.' }),
    );
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="git-cleanup-error"]')?.textContent).toContain('no resolvable git repository');
    httpCtrl.verify();
  });
});
