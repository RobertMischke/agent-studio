import { CliOutputLine } from '../models/job.model';

export type ActivityLogKind = 'read' | 'search' | 'command' | 'edit' | 'task' | 'todo' | 'error' | 'message' | 'other';
export type ActivityLogFilters = Record<ActivityLogKind, boolean>;

export interface ActivityLogGroup {
  id: string;
  kind: ActivityLogKind;
  title: string;
  subtitle: string;
  status: 'ok' | 'error' | 'neutral';
  lines: CliOutputLine[];
  collapsedByDefault: boolean;
}

const actionStartRegex = /^(?<marker>[^\w\s]+|x|X|\*)\s+(?<label>.+)$/i;

export const activityLogKinds: ActivityLogKind[] = ['read', 'search', 'command', 'edit', 'task', 'todo', 'error', 'message', 'other'];

export const defaultActivityLogFilters: ActivityLogFilters = {
  read: true,
  search: true,
  command: true,
  edit: true,
  task: true,
  todo: true,
  error: true,
  message: true,
  other: true
};

export function parseActivityLog(lines: CliOutputLine[]): ActivityLogGroup[] {
  const groups: ActivityLogGroup[] = [];
  let current: ActivityLogGroup | null = null;

  for (const line of lines) {
    const action = parseActionLine(line);
    if (action) {
      current = {
        id: `${groups.length}-${line.timestamp}-${action.title}`,
        kind: action.kind,
        title: action.title,
        subtitle: '',
        status: action.status,
        lines: [line],
        collapsedByDefault: false
      };
      groups.push(current);
      continue;
    }

    if (isBlank(line.text)) {
      if (current) current.lines.push(line);
      continue;
    }

    if (current && isContinuation(line.text)) {
      current.lines.push(line);
      if (!current.subtitle) {
        current.subtitle = cleanContinuation(line.text);
      }
      if (line.stream === 'stderr' || /error|failed|exited with error/i.test(line.text)) {
        current.status = 'error';
      }
      continue;
    }

    const kind: ActivityLogKind = line.stream === 'stderr' || /error|failed|exited with error/i.test(line.text)
      ? 'error'
      : 'message';
    current = {
      id: `${groups.length}-${line.timestamp}-message`,
      kind,
      title: line.text,
      subtitle: '',
      status: kind === 'error' ? 'error' : 'neutral',
      lines: [line],
      collapsedByDefault: false
    };
    groups.push(current);
  }

  return compressActivityGroups(groups);
}

export function filterActivityGroups(groups: ActivityLogGroup[], filters: ActivityLogFilters): ActivityLogGroup[] {
  return groups.filter((group) => filters[group.kind]);
}

export function flattenActivityLines(groups: ActivityLogGroup[]): CliOutputLine[] {
  return groups.flatMap((group) => group.lines);
}

export function activityKindLabel(kind: ActivityLogKind): string {
  switch (kind) {
    case 'read': return 'Reading files';
    case 'search': return 'Searches';
    case 'command': return 'Commands';
    case 'edit': return 'Edits';
    case 'task': return 'Tasks';
    case 'todo': return 'Todos';
    case 'error': return 'Errors';
    case 'message': return 'Messages';
    case 'other': return 'Other';
  }
}

function parseActionLine(line: CliOutputLine): { kind: ActivityLogKind; title: string; status: 'ok' | 'error' | 'neutral' } | null {
  const match = actionStartRegex.exec(line.text);
  if (!match?.groups) return null;

  const label = match.groups['label'].trim();
  const marker = match.groups['marker'];
  const status = line.stream === 'stderr' || marker.toLowerCase() === 'x' || /exited with error|failed/i.test(label)
    ? 'error'
    : 'ok';

  return {
    kind: classifyAction(label, status),
    title: label,
    status
  };
}

function classifyAction(label: string, status: 'ok' | 'error' | 'neutral'): ActivityLogKind {
  if (status === 'error') return 'error';
  if (/^Read\b/i.test(label)) return 'read';
  if (/^Search\b/i.test(label)) return 'search';
  if (/\(shell\)|^Run\b|^Execute|^Executing|^Build|^Check\b/i.test(label)) return 'command';
  if (/^Edit\b|^Write\b|^Create\b|^Delete\b|^Move\b|^Update\b|^Apply\b/i.test(label)) return 'edit';
  if (/^Task\b/i.test(label)) return 'task';
  if (/^Todo\b/i.test(label)) return 'todo';
  return 'other';
}

function compressActivityGroups(groups: ActivityLogGroup[]): ActivityLogGroup[] {
  const output: ActivityLogGroup[] = [];
  let index = 0;

  while (index < groups.length) {
    const group = groups[index];
    if (!isCompressible(group)) {
      output.push(group);
      index += 1;
      continue;
    }

    const batch = [group];
    index += 1;
    while (index < groups.length && groups[index].kind === group.kind && groups[index].status === group.status) {
      batch.push(groups[index]);
      index += 1;
    }

    if (batch.length === 1) {
      output.push(group);
      continue;
    }

    const lines = batch.flatMap((item) => item.lines);
    output.push({
      id: `${group.id}-batch-${batch.length}`,
      kind: group.kind,
      title: `${activityKindLabel(group.kind)} (${batch.length})`,
      subtitle: batch.map((item) => item.subtitle || item.title).filter(Boolean).slice(0, 3).join(', '),
      status: group.status,
      lines,
      collapsedByDefault: true
    });
  }

  return output;
}

function isCompressible(group: ActivityLogGroup): boolean {
  return group.kind === 'read' || group.kind === 'search';
}

function isContinuation(text: string): boolean {
  return /^\s/.test(text) || /^[|`\\/_-]/.test(text);
}

function cleanContinuation(text: string): string {
  return text.replace(/^[\s|`\\/_-]+/, '').trim();
}

function isBlank(text: string): boolean {
  return text.trim().length === 0;
}
