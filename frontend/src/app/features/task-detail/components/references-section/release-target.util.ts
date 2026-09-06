import {
  TaskInfo,
  TaskReferenceKind,
  TaskReferences,
  TaskState,
  taskDependencyKey,
  taskDependencyRequiresRelease,
} from '../../../../models/task.model';

export interface ReleaseTarget {
  jobId: string;
  key: string;
  watchPath: string;
  released: boolean;
}

export function isTerminalTaskState(state: string): boolean {
  return state === TaskState.Completed || state === TaskState.Archive;
}

export function selfReleaseTarget(
  info: TaskInfo,
  overrides: ReadonlyMap<string, boolean>,
): ReleaseTarget | null {
  if (!isTerminalTaskState(info.state)) return null;
  return withOverride({
    jobId: info.id,
    key: info.key ?? info.displayKey ?? info.id,
    watchPath: info.watchPath,
    released: info.released === true,
  }, overrides);
}

export function dependencyReleaseTarget(
  info: TaskInfo,
  refs: TaskReferences,
  keyIndex: ReadonlyMap<string, TaskInfo>,
  overrides: ReadonlyMap<string, boolean>,
  kind: TaskReferenceKind,
  key: string,
): ReleaseTarget | null {
  if (kind !== 'dependsOn') return null;
  const normalized = key.trim().toUpperCase();
  const edge = refs.dependsOn.find(item => taskDependencyKey(item).trim().toUpperCase() === normalized);
  if (!edge || !taskDependencyRequiresRelease(edge)) return null;
  const wait = info.waitsOn?.items.find(item => item.key.trim().toUpperCase() === normalized);
  if (wait?.targetJobId && wait.targetWatchPath && isTerminalTaskState(wait.targetState ?? '')) {
    return withOverride({
      jobId: wait.targetJobId,
      key: wait.key,
      watchPath: wait.targetWatchPath,
      released: wait.targetReleased === true,
    }, overrides);
  }
  const target = keyIndex.get(normalized);
  if (!target || !isTerminalTaskState(target.state)) return null;
  return withOverride({
    jobId: target.id,
    key: target.key ?? key,
    watchPath: target.watchPath,
    released: target.released === true,
  }, overrides);
}

export function releaseIdentity(target: Pick<ReleaseTarget, 'jobId' | 'watchPath'>): string {
  return `${target.watchPath}::${target.jobId}`;
}

function withOverride(
  target: ReleaseTarget,
  overrides: ReadonlyMap<string, boolean>,
): ReleaseTarget {
  return { ...target, released: overrides.get(releaseIdentity(target)) ?? target.released };
}
