import type { TaskReferenceStatus } from '../../../../components/task-reference-microcard/task-reference-microcard';
import type { WorkbenchOverview, WorkbenchOverviewItem } from '../../../../models/project-docs.model';

export interface WorkbenchReferenceBatch {
  keys: string[];
  keysByItem: ReadonlyMap<string, readonly string[]>;
}

export function workbenchItemIdentity(item: WorkbenchOverviewItem): string {
  return `${item.projectName}:${item.workbench.id}`;
}

export function workbenchReferenceBatch(overview: WorkbenchOverview): WorkbenchReferenceBatch {
  const keysByItem = new Map<string, string[]>();
  const keys: string[] = [];
  const seen = new Set<string>();
  for (const item of overview.items) {
    if (!['decision-pending', 'active', 'decided'].includes(item.workbench.status)) continue;
    const itemKeys = uniqueKeys([
      ...(item.workbench.documentation?.references.map(reference => reference.key) ?? []),
      ...(item.workbench.relatedTaskKeys ?? []),
    ]);
    keysByItem.set(workbenchItemIdentity(item), itemKeys);
    for (const key of itemKeys) {
      const normalized = normalizeKey(key);
      if (seen.has(normalized)) continue;
      seen.add(normalized);
      keys.push(key);
    }
  }
  return { keys, keysByItem };
}

export function statusesByWorkbenchItem(
  batch: WorkbenchReferenceBatch,
  resolved: readonly TaskReferenceStatus[],
  items: readonly WorkbenchOverviewItem[],
): ReadonlyMap<string, readonly TaskReferenceStatus[]> {
  const byKey = new Map(resolved.map(status => [normalizeKey(status.key), status] as const));
  const projectByItem = new Map(items.map(item => [workbenchItemIdentity(item), item.projectName]));
  return new Map([...batch.keysByItem].map(([itemKey, keys]) => [
    itemKey,
    keys.map(key => byKey.get(normalizeKey(key)) ?? ghostStatus(key, projectByItem.get(itemKey) ?? '')),
  ]));
}

function uniqueKeys(keys: readonly string[]): string[] {
  const result: string[] = [];
  const seen = new Set<string>();
  for (const key of keys) {
    const normalized = normalizeKey(key);
    if (!normalized || seen.has(normalized)) continue;
    seen.add(normalized);
    result.push(key.trim());
  }
  return result;
}

function normalizeKey(value: string | null | undefined): string {
  return (value ?? '').trim().toUpperCase();
}

function ghostStatus(key: string, projectName: string): TaskReferenceStatus {
  return {
    key,
    exists: false,
    taskKey: null,
    title: null,
    lane: null,
    projectId: '',
    projectName,
    projectColor: null,
    merge: null,
    reviewGrade: null,
  };
}
