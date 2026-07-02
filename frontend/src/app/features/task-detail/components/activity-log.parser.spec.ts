// Specs for the app-side activity-log helpers. Coverage for the canonical
// grouper (`parseActivityLog`) and turn builder (`buildConversationTurns`)
// moved to the @coding-agent/chat library together with the implementations.
import { describe, expect, it } from 'vitest';
import {
  binToolBurstByKind,
  deriveLiveStatus,
  formatBurstDuration,
  formatLiveSince,
  parseActivityLog,
  summarizeToolBurst
} from './activity-log.parser';
import { CliOutputLine } from '../../../models/task.model';

describe('summarizeToolBurst', () => {
  it('counts batched groups by their batch size, not by group count', () => {
    const groups = parseActivityLog([
      line('* Read prompt.md'),
      line('  | prompt.md'),
      line('* Read status.md'),
      line('  | status.md'),
      line('* Read job.json'),
      line('  | job.json')
    ]);
    // The parser compresses adjacent reads into one group with title
    // "Reading files ×3"; the summary must recover the original count of 3.
    const summary = summarizeToolBurst(groups);
    expect(summary.total).toBe(3);
    expect(summary.counts.read).toBe(3);
  });

  it('measures the wall-clock span of the burst', () => {
    const groups = parseActivityLog([
      line('* Read prompt.md', 'stdout', '2026-04-26T12:00:00.000Z'),
      line('  | prompt.md', 'stdout', '2026-04-26T12:00:00.500Z'),
      line('* Search "foo"', 'stdout', '2026-04-26T12:00:04.500Z'),
      line('  | foo', 'stdout', '2026-04-26T12:00:04.800Z')
    ]);
    const summary = summarizeToolBurst(groups);
    // 4.8s span between first and last timestamp
    expect(summary.durationMs).toBe(4800);
  });

  it('binToolBurstByKind groups underlying entries per kind for the expanded view', () => {
    const groups = parseActivityLog([
      line('* Read a.ts'),
      line('  | a.ts'),
      line('* Read b.ts'),
      line('  | b.ts'),
      line('* Search "needle"'),
      line('  | needle'),
      line('* Read c.ts'),
      line('  | c.ts')
    ]);
    const bins = binToolBurstByKind(groups);
    const byKind = Object.fromEntries(bins.map((b) => [b.kind, b.count]));
    // 2 reads (compressed) + 1 search + 1 read = 3 reads, 1 search across two read bins.
    // binToolBurstByKind merges them by kind.
    expect(byKind['read']).toBe(3);
    expect(byKind['search']).toBe(1);
  });
});

describe('formatBurstDuration', () => {
  it('formats sub-second, second, and minute spans compactly', () => {
    expect(formatBurstDuration(0)).toBe('');
    expect(formatBurstDuration(250)).toBe('<1s');
    expect(formatBurstDuration(4500)).toBe('5s');
    expect(formatBurstDuration(60_000)).toBe('1m');
    expect(formatBurstDuration(80_000)).toBe('1m 20s');
    expect(formatBurstDuration(3_600_000)).toBe('1h');
    expect(formatBurstDuration(3_660_000)).toBe('1h 1m');
  });
});

describe('deriveLiveStatus', () => {
  const T = '2026-04-26T12:00:00.000Z';
  const NOW = Date.parse(T) + 4_000; // 4 seconds after the last line

  it('returns null when the run is not active', () => {
    const status = deriveLiveStatus([line('* Read prompt.md', 'stdout', T)], false, NOW);
    expect(status).toBeNull();
  });

  it('reports a Starting state when the buffer is empty but the run has begun', () => {
    const status = deriveLiveStatus([], true, NOW);
    expect(status).not.toBeNull();
    expect(status!.kind).toBe('starting');
    expect(status!.verb).toMatch(/Starting/i);
  });

  it('names the file when the latest action is a single Read', () => {
    const status = deriveLiveStatus([
      line('* Read prompt.md', 'stdout', T),
      line('  | prompt.md', 'stdout', T)
    ], true, NOW);
    expect(status!.kind).toBe('tool');
    expect(status!.verb).toBe('Reading');
    expect(status!.detail).toBe('prompt.md');
  });

  it('aggregates a batched read burst into a count detail', () => {
    const status = deriveLiveStatus([
      line('* Read a.ts', 'stdout', T),
      line('  | a.ts', 'stdout', T),
      line('* Read b.ts', 'stdout', T),
      line('  | b.ts', 'stdout', T),
      line('* Read c.ts', 'stdout', T),
      line('  | c.ts', 'stdout', T)
    ], true, NOW);
    expect(status!.kind).toBe('tool');
    expect(status!.verb).toBe('Reading');
    expect(status!.detail).toBe('3 files');
  });

  it('classifies search, edit, and command actions with their own verbs', () => {
    const search = deriveLiveStatus(
      [line('* Search "needle"', 'stdout', T)], true, NOW)!;
    expect(search.verb).toBe('Searching');

    const edit = deriveLiveStatus(
      [line('* Edit src/foo.ts', 'stdout', T)], true, NOW)!;
    expect(edit.verb).toBe('Editing');
    expect(edit.detail).toBe('src/foo.ts');

    const cmd = deriveLiveStatus(
      [line('* Run npm test (shell)', 'stdout', T)], true, NOW)!;
    expect(cmd.verb).toBe('Running');
  });

  it('falls back to "Thinking" for free-form agent text', () => {
    const status = deriveLiveStatus([
      line('Looking at the activity-log component to understand the chat surface.', 'stdout', T)
    ], true, NOW)!;
    expect(status.kind).toBe('agent');
    expect(status.verb).toBe('Thinking');
    expect(status.detail).toBe('');
  });

  it('reports "Working on your message" right after a user follow-up', () => {
    const status = deriveLiveStatus([
      line('* Read prompt.md', 'stdout', T),
      line('please continue', 'user', T)
    ], true, NOW)!;
    expect(status.kind).toBe('user');
    expect(status.verb).toMatch(/your message/i);
  });

  it('skips taskboard runtime markers when picking the last meaningful group', () => {
    const status = deriveLiveStatus([
      line('* Read prompt.md', 'stdout', T),
      line('  | prompt.md', 'stdout', T),
      line('[taskboard] checkpoint', 'system', T)
    ], true, NOW)!;
    expect(status.verb).toBe('Reading');
    expect(status.detail).toBe('prompt.md');
  });

  it('counts seconds since the last log line', () => {
    const lastTs = '2026-04-26T12:00:00.000Z';
    const now = Date.parse(lastTs) + 7_500; // 7.5 s later
    const status = deriveLiveStatus([line('* Read prompt.md', 'stdout', lastTs)], true, now)!;
    // 7.5 s -> rounded sinceMs is at least the gap.
    expect(status.sinceMs).toBeGreaterThanOrEqual(7_000);
    expect(status.sinceMs).toBeLessThanOrEqual(8_000);
  });
});

describe('formatLiveSince', () => {
  it('hides sub-second values, then renders compact "Ns / Nm Ns / Nh Nm"', () => {
    expect(formatLiveSince(0)).toBe('');
    expect(formatLiveSince(800)).toBe('');
    expect(formatLiveSince(2_000)).toBe('2s');
    expect(formatLiveSince(47_000)).toBe('47s');
    expect(formatLiveSince(60_000)).toBe('1m');
    expect(formatLiveSince(72_000)).toBe('1m 12s');
    expect(formatLiveSince(3_600_000)).toBe('1h');
    expect(formatLiveSince(3_900_000)).toBe('1h 5m');
  });
});

function line(text: string, stream = 'stdout', timestamp = '2026-04-26T12:00:00.000Z'): CliOutputLine {
  return {
    timestamp,
    stream,
    text
  };
}

