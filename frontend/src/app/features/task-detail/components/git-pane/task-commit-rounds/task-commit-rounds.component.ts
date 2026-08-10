import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import type { TaskCommitInfo } from '../../../../git';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { formatCompactDateTime, formatDateTime } from '../../../../../services/format.util';

interface SupersededRound {
  key: string;
  label: string;
  commits: TaskCommitInfo[];
}

@Component({
  selector: 'app-task-commit-rounds',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective],
  templateUrl: './task-commit-rounds.component.html',
  styleUrl: './task-commit-rounds.component.scss',
})
export class TaskCommitRoundsComponent {
  readonly commits = input.required<TaskCommitInfo[]>();
  readonly selectedSha = input<string | null>(null);
  readonly filesCount = input(0);
  readonly selectSha = output<string | null>();

  readonly collapsed = signal(readCollapsed());
  readonly activeCommits = computed(() =>
    this.commits().filter((commit) => !commit.supersededByAttempt?.trim()),
  );
  readonly supersededRounds = computed<SupersededRound[]>(() => buildSupersededRounds(this.commits()));
  readonly summary = computed(() => {
    if (this.selectedSha() === null && this.activeCommits().length > 1) {
      const files = this.filesCount();
      return `All ${this.activeCommits().length} commits · ${files} ${files === 1 ? 'file' : 'files'}`;
    }
    const selected = this.commits().find((commit) => commit.sha === this.selectedSha());
    return selected
      ? `${selected.shortSha} · ${selected.message.split('\n')[0]}`
      : `${this.activeCommits().length} current task commits`;
  });

  toggleCollapsed(): void {
    const next = !this.collapsed();
    this.collapsed.set(next);
    writeCollapsed(next);
  }

  tooltip(entry: TaskCommitInfo, index: number, total: number): string {
    return `${index + 1}/${total} · ${entry.shortSha} · ${formatDateTime(entry.at)} · ${entry.message}`;
  }

  timestamp(entry: TaskCommitInfo): string {
    return formatCompactDateTime(entry.at);
  }
}

function buildSupersededRounds(commits: TaskCommitInfo[]): SupersededRound[] {
  const attemptIds: string[] = [];
  for (const commit of commits) {
    const attempt = commit.runAttemptId?.trim();
    if (attempt && !attemptIds.includes(attempt)) attemptIds.push(attempt);
  }

  const groups = new Map<string, { sourceAttempt: string | null; replacement: string; commits: TaskCommitInfo[] }>();
  commits.forEach((commit, index) => {
    const replacement = commit.supersededByAttempt?.trim();
    if (!replacement) return;
    const sourceAttempt = commit.runAttemptId?.trim() || null;
    const key = `${sourceAttempt ?? `legacy-${index + 1}`}|${replacement}`;
    const existing = groups.get(key);
    if (existing) existing.commits.push(commit);
    else groups.set(key, { sourceAttempt, replacement, commits: [commit] });
  });

  return [...groups.entries()].map(([key, group], groupIndex) => {
    const sourceIndex = group.sourceAttempt ? attemptIds.indexOf(group.sourceAttempt) : -1;
    const sourceRound = sourceIndex >= 0 ? sourceIndex + 1 : groupIndex + 1;
    const replacementCommit = commits.find((commit) =>
      commit.runAttemptId === group.replacement
      || commit.resultSha === group.replacement
      || commit.sha === group.replacement);
    const replacementAttempt = replacementCommit?.runAttemptId ?? group.replacement;
    const replacementIndex = attemptIds.indexOf(replacementAttempt);
    const replacementLabel = group.replacement === 'next-attempt'
      ? 'next attempt'
      : `round ${replacementIndex >= 0 ? replacementIndex + 1 : sourceRound + 1}`;
    return {
      key,
      label: `Round ${sourceRound}, replaced by ${replacementLabel}`,
      commits: group.commits,
    };
  });
}

const COLLAPSED_KEY = 'taskboard.gitPane.commitGroupCollapsed';

function readCollapsed(): boolean {
  try {
    const stored = localStorage.getItem(COLLAPSED_KEY);
    return stored === null ? true : stored === '1';
  }
  catch { return true; }
}

function writeCollapsed(value: boolean): void {
  try { localStorage.setItem(COLLAPSED_KEY, value ? '1' : '0'); }
  catch { /* ignore quota / privacy-mode errors */ }
}
