import type { GitGraphCommit, GitTaskBadge } from './git.model';

export type GitCommitChipKind = 'presence' | 'deployment' | 'task' | 'work';
export type GitCommitChipTone =
  | 'integrated'
  | 'released'
  | 'deployed'
  | 'task'
  | 'in-progress'
  | 'remote';

/** One quiet, user-facing fact in a commit row. Raw ref names stay out of the row. */
export interface GitCommitChip {
  readonly id: string;
  readonly kind: GitCommitChipKind;
  readonly tone: GitCommitChipTone;
  readonly label: string;
  readonly detail: string;
  readonly task: GitTaskBadge | null;
}

/**
 * Projects repository facts into the single commit-chip vocabulary used by the
 * Project Hub: presence, deployment, card, then current work state.
 */
export function buildGitCommitChips(commit: GitGraphCommit): GitCommitChip[] {
  const chips: GitCommitChip[] = [];
  const presence = commit.presence;

  if (presence?.inIntegration) {
    chips.push(chip(
      `presence:integrated:${presence.integrationBranch}`,
      'presence',
      'integrated',
      `Integrated · ${presence.integrationBranch}`,
      `This commit is contained in the integration branch ${presence.integrationBranch}.`,
    ));
  }
  if (presence?.inRelease) {
    chips.push(chip(
      `presence:released:${presence.releaseBranch}`,
      'presence',
      'released',
      `Released · ${presence.releaseBranch}`,
      `This commit is contained in the release branch ${presence.releaseBranch}.`,
    ));
  }

  for (const deployment of uniqueBy(commit.deployments ?? [], item => item.target.toLowerCase())) {
    chips.push(chip(
      `deployment:${deployment.target}`,
      'deployment',
      'deployed',
      `Deployed · ${deployment.target}`,
      `The ${deployment.target} runtime reports this exact build commit.`,
    ));
  }

  for (const task of uniqueBy(commit.tasks ?? [], item => item.taskKey)) {
    chips.push({
      id: `task:${task.taskKey}`,
      kind: 'task',
      tone: 'task',
      label: task.key,
      detail: `Open card ${task.key}: ${task.title}`,
      task,
    });
  }

  const inProgress = (commit.tasks ?? []).filter(task => task.lane === '3-progress');
  if (inProgress.length > 0) {
    const cards = inProgress.map(task => task.key).join(', ');
    chips.push(chip(
      'work:in-progress',
      'work',
      'in-progress',
      'In progress',
      `${cards} ${inProgress.length === 1 ? 'is' : 'are'} currently in the In Progress lane.`,
    ));
  }

  const remoteRefs = uniqueBy(
    (commit.refs ?? []).filter(ref => ref.isRemote),
    ref => ref.name.toLowerCase(),
  );
  if (remoteRefs.length > 0) {
    const refs = remoteRefs.map(ref => ref.name).join(', ');
    chips.push(chip(
      'work:remote',
      'work',
      'remote',
      'Remote',
      `Remote ${remoteRefs.length === 1 ? 'ref' : 'refs'} at this commit: ${refs}.`,
    ));
  }

  return chips;
}

function chip(
  id: string,
  kind: GitCommitChipKind,
  tone: GitCommitChipTone,
  label: string,
  detail: string,
): GitCommitChip {
  return { id, kind, tone, label, detail, task: null };
}

function uniqueBy<T>(items: readonly T[], key: (item: T) => string): T[] {
  const seen = new Set<string>();
  return items.filter(item => {
    const value = key(item);
    if (seen.has(value)) return false;
    seen.add(value);
    return true;
  });
}
