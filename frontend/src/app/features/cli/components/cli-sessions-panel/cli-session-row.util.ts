/**
 * Pure helpers for the CLI-session tool: flatten the nested usage report into a
 * single virtualisable row list, and filter / sort / format those rows. Kept
 * out of the component so the presentation controller stays inside its size
 * budget and the list transforms are unit-testable in isolation.
 */
import { cliTypeIcon, cliTypeLabel } from '../../../../services/format.util';
import type { CliType } from '../../../../models/task.model';
import type { CliUsageReport, LinkedJobRef } from '../../../../features/cli';

/** One flattened, denormalised session row — the unit the virtual list renders. */
export interface SessionRow {
  /** Stable identity for trackBy: `${cliType}:${id}:${projectName}`. */
  key: string;
  cliType: CliType;
  cliLabel: string;
  cliIcon: string;
  projectName: string;
  rootPath: string | null;
  id: string;
  label: string | null;
  updatedAt: string | null;
  sizeBytes: number;
  tokens: string | null;
  isProjectDefault: boolean;
  linkedJob: LinkedJobRef | null;
  /** Lowercased haystack precomputed once for search. */
  haystack: string;
}

export type SessionSortKey = 'recent' | 'size' | 'project' | 'cli';
export type CliFilter = 'all' | CliType;

/** Flatten the nested {section -> project -> sessions} report into one row list. */
export function buildRows(report: CliUsageReport | null): SessionRow[] {
  const rows: SessionRow[] = [];
  for (const section of report?.sections ?? []) {
    const cliType = section.cliType as CliType;
    const cliLabel = cliTypeLabel(cliType);
    const cliIcon = cliTypeIcon(cliType);
    for (const project of section.projects) {
      for (const s of project.sessions) {
        const tokens = s.lastUsage?.tokens ?? null;
        const haystack = [
          s.id,
          s.label ?? '',
          project.projectName,
          project.rootPath ?? '',
          s.cwd ?? '',
          cliLabel,
          s.linkedJob?.title ?? '',
        ]
          .join(' ')
          .toLowerCase();
        rows.push({
          key: `${cliType}:${s.id}:${project.projectName}`,
          cliType,
          cliLabel,
          cliIcon,
          projectName: project.projectName,
          rootPath: project.rootPath ?? s.cwd ?? null,
          id: s.id,
          label: s.label,
          updatedAt: s.updatedAt,
          sizeBytes: s.sizeBytes ?? 0,
          tokens,
          isProjectDefault: s.isProjectDefault,
          linkedJob: s.linkedJob,
          haystack,
        });
      }
    }
  }
  return rows;
}

/** Per-CLI row counts for the filter chips (each chip label reconciles to the sum). */
export function countByCli(rows: SessionRow[]): Record<string, number> {
  const out: Record<string, number> = {};
  for (const r of rows) out[r.cliType] = (out[r.cliType] ?? 0) + 1;
  return out;
}

export function filterRows(
  rows: SessionRow[],
  cli: CliFilter,
  query: string,
  linkedOnly: boolean,
): SessionRow[] {
  const q = query.trim().toLowerCase();
  const terms = q.length ? q.split(/\s+/) : [];
  return rows.filter((r) => {
    if (cli !== 'all' && r.cliType !== cli) return false;
    if (linkedOnly && !r.linkedJob) return false;
    if (terms.length && !terms.every((t) => r.haystack.includes(t))) return false;
    return true;
  });
}

export function sortRows(rows: SessionRow[], key: SessionSortKey): SessionRow[] {
  const copy = rows.slice();
  const time = (r: SessionRow) => (r.updatedAt ? new Date(r.updatedAt).getTime() : 0);
  switch (key) {
    case 'size':
      copy.sort((a, b) => b.sizeBytes - a.sizeBytes || time(b) - time(a));
      break;
    case 'project':
      copy.sort(
        (a, b) => a.projectName.localeCompare(b.projectName) || time(b) - time(a),
      );
      break;
    case 'cli':
      copy.sort((a, b) => a.cliLabel.localeCompare(b.cliLabel) || time(b) - time(a));
      break;
    case 'recent':
    default:
      copy.sort((a, b) => time(b) - time(a));
      break;
  }
  return copy;
}

/** Human-readable byte size; renders "—" for the index-only (0-byte) case. */
export function formatSize(bytes: number): string {
  if (!bytes || bytes <= 0) return '—';
  const units = ['B', 'KB', 'MB', 'GB'];
  let n = bytes;
  let u = 0;
  while (n >= 1024 && u < units.length - 1) {
    n /= 1024;
    u++;
  }
  return `${n < 10 && u > 0 ? n.toFixed(1) : Math.round(n)} ${units[u]}`;
}

/** Shorten a session id for the row; the full id lives in a tooltip / detail. */
export function shortId(id: string): string {
  return id.length <= 12 ? id : id.slice(0, 8) + '…';
}

/**
 * Collapse a long absolute path to its trailing segments so the row stays one
 * line while still identifying the checkout. The full path is copyable.
 */
export function tailPath(path: string | null, segments = 2): string {
  if (!path) return '';
  const parts = path.split(/[\\/]+/).filter(Boolean);
  if (parts.length <= segments) return path;
  return '…/' + parts.slice(-segments).join('/');
}

/** Lane -> compact chip presentation, mirroring the task-microcard tone vocabulary (AGT-2050). */
export function taskChipTone(lane: string | null, isActive: boolean): string {
  if (isActive) return 'active';
  if (!lane) return 'ghost';
  if (lane === '6-completed' || lane === '7-archive') return 'done';
  if (lane === '3-progress' || lane === '4-auto-review') return 'active';
  if (lane === '5-human-review' || lane === '5e-escalated' || lane === '3b-code-not-complete')
    return 'waiting';
  return 'queued';
}
