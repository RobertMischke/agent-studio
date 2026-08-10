import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { TaskCommitInfo } from '../../../../git';
import { TaskCommitRoundsComponent } from './task-commit-rounds.component';

const superseded: TaskCommitInfo = {
  sha: '1'.repeat(40),
  shortSha: '1111111',
  message: 'wip(runner): salvage before teardown - outcome Done',
  filesChanged: 2,
  files: ['backend/a.cs', 'frontend/a.ts'],
  at: '2026-08-09T10:00:00Z',
  runAttemptId: 'run-round-1',
  supersededByAttempt: 'run-round-2',
};

const current: TaskCommitInfo = {
  sha: '2'.repeat(40),
  shortSha: '2222222',
  message: 'feat(AGT-2533): replacement delivery',
  filesChanged: 2,
  files: ['backend/a.cs', 'frontend/a.ts'],
  at: '2026-08-09T11:00:00Z',
  runAttemptId: 'run-round-2',
};

describe('TaskCommitRoundsComponent', () => {
  beforeEach(() => localStorage.removeItem('taskboard.gitPane.commitGroupCollapsed'));

  it('keeps a replaced round separate from current commits and labels the replacement', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCommitRoundsComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskCommitRoundsComponent);
    fixture.componentRef.setInput('commits', [superseded, current]);
    fixture.componentRef.setInput('selectedSha', current.sha);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('[data-testid="git-commit-group-toggle"]')?.click();
    fixture.detectChanges();
    root.querySelector<HTMLDetailsElement>('[data-testid="git-superseded-rounds"]')!.open = true;
    fixture.detectChanges();

    expect(root.querySelectorAll('[data-testid="git-commit-chain-item"]')).toHaveLength(1);
    expect(root.querySelector('[data-testid="git-commit-chain-all"]')).toBeNull();
    expect(root.querySelector('[data-testid="git-superseded-round"]')?.textContent)
      .toContain('Round 1, replaced by round 2');
    expect(root.querySelector('[data-testid="git-superseded-commit"]')?.getAttribute('data-sha'))
      .toBe(superseded.sha);
  });
});
