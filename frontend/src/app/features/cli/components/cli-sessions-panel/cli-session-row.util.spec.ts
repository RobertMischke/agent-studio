import { describe, expect, it } from 'vitest';
import {
  buildRows,
  countByCli,
  filterRows,
  formatSize,
  shortId,
  sortRows,
  tailPath,
  taskChipTone,
} from './cli-session-row.util';
import type { CliUsageReport } from '../../../../features/cli';

function report(): CliUsageReport {
  return {
    at: '2026-07-11T00:00:00Z',
    sections: [
      {
        cliType: 'claude',
        available: true,
        version: '1.0',
        path: null,
        error: null,
        projects: [
          {
            projectName: 'alpha',
            rootPath: 'C:/Projects/alpha',
            sessions: [
              {
                id: 'aaaa1111-2222',
                label: 'first prompt here',
                updatedAt: '2026-07-10T10:00:00Z',
                cwd: 'C:/Projects/alpha',
                sizeBytes: 2048,
                lastUsage: { at: '', tokens: '1.2k', changes: null, requests: null },
                isProjectDefault: false,
                linkedJob: {
                  jobId: 'j1',
                  title: 'AGT-1',
                  watchPath: 'w',
                  projectName: 'alpha',
                  lane: '3-progress',
                  isActive: true,
                },
              },
              {
                id: 'bbbb3333-4444',
                label: null,
                updatedAt: '2026-07-09T10:00:00Z',
                cwd: 'C:/Projects/alpha',
                sizeBytes: 10_000,
                lastUsage: null,
                isProjectDefault: true,
                linkedJob: null,
              },
            ],
          },
        ],
      },
      {
        cliType: 'codex',
        available: true,
        version: '2.0',
        path: null,
        error: null,
        projects: [
          {
            projectName: 'beta',
            rootPath: 'C:/Projects/beta',
            sessions: [
              {
                id: 'cccc5555-6666',
                label: 'codex thread',
                updatedAt: '2026-07-11T10:00:00Z',
                cwd: 'C:/Projects/beta',
                sizeBytes: 0,
                lastUsage: null,
                isProjectDefault: false,
                linkedJob: null,
              },
            ],
          },
        ],
      },
    ],
  };
}

describe('cli-session-row util', () => {
  it('flattens the nested report into one row per session', () => {
    const rows = buildRows(report());
    expect(rows).toHaveLength(3);
    expect(rows.map((r) => r.cliType).sort()).toEqual(['claude', 'claude', 'codex']);
    expect(rows[0].tokens).toBe('1.2k');
    expect(rows[0].key).toBe('claude:aaaa1111-2222:alpha');
  });

  it('counts rows per CLI (chip totals reconcile to the sum)', () => {
    const rows = buildRows(report());
    const counts = countByCli(rows);
    expect(counts['claude']).toBe(2);
    expect(counts['codex']).toBe(1);
    expect(counts['claude'] + counts['codex']).toBe(rows.length);
  });

  it('filters by CLI, free-text and linked-only', () => {
    const rows = buildRows(report());
    expect(filterRows(rows, 'codex', '', false)).toHaveLength(1);
    expect(filterRows(rows, 'all', 'beta', false)).toHaveLength(1);
    expect(filterRows(rows, 'all', 'AGT-1', false)).toHaveLength(1);
    expect(filterRows(rows, 'all', '', true)).toHaveLength(1); // only the linked one
    expect(filterRows(rows, 'all', 'nothing-matches', false)).toHaveLength(0);
  });

  it('sorts by recent, size, project and cli', () => {
    const rows = buildRows(report());
    expect(sortRows(rows, 'recent')[0].id).toBe('cccc5555-6666'); // newest
    expect(sortRows(rows, 'size')[0].sizeBytes).toBe(10_000); // biggest
    expect(sortRows(rows, 'project')[0].projectName).toBe('alpha');
    expect(sortRows(rows, 'cli')[0].cliLabel).toBe('Claude Code');
  });

  it('formats byte sizes and treats 0 as unknown', () => {
    expect(formatSize(0)).toBe('—');
    expect(formatSize(512)).toBe('512 B');
    expect(formatSize(2048)).toBe('2.0 KB');
    expect(formatSize(5_242_880)).toBe('5.0 MB');
  });

  it('shortens ids and tails long paths', () => {
    expect(shortId('aaaa1111-2222-3333')).toBe('aaaa1111…');
    expect(shortId('short')).toBe('short');
    expect(tailPath('C:/Projects/agent/frontend')).toBe('…/agent/frontend');
    expect(tailPath('C:/only')).toBe('C:/only');
    expect(tailPath(null)).toBe('');
  });

  it('maps lane + active flag to a chip tone', () => {
    expect(taskChipTone('3-progress', true)).toBe('active');
    expect(taskChipTone('6-completed', false)).toBe('done');
    expect(taskChipTone('5-human-review', false)).toBe('waiting');
    expect(taskChipTone('2-ready', false)).toBe('queued');
    expect(taskChipTone(null, false)).toBe('ghost');
  });
});
