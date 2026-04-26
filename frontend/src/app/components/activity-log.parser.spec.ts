import { describe, expect, it } from 'vitest';
import {
  defaultActivityLogFilters,
  filterActivityGroups,
  flattenActivityLines,
  parseActivityLog
} from './activity-log.parser';
import { CliOutputLine } from '../models/job.model';

describe('parseActivityLog', () => {
  it('compresses adjacent read entries into a single expandable group', () => {
    const groups = parseActivityLog([
      line('* Read prompt.md'),
      line('  | prompt.md'),
      line('* Read status.md'),
      line('  | status.md'),
      line('* Read job-detail.ts'),
      line('  | frontend/src/app/components/job-detail.ts')
    ]);

    expect(groups).toHaveLength(1);
    expect(groups[0].kind).toBe('read');
    expect(groups[0].title).toBe('Reading files (3)');
    expect(groups[0].collapsedByDefault).toBe(true);
    expect(groups[0].lines).toHaveLength(6);
  });

  it('classifies shell output and failed tool calls', () => {
    const groups = parseActivityLog([
      line('* Baseline frontend build (shell)'),
      line('  | npm run build'),
      line('x Read prompt.md'),
      line('  | Path does not exist')
    ]);

    expect(groups[0].kind).toBe('command');
    expect(groups[0].status).toBe('ok');
    expect(groups[1].kind).toBe('error');
    expect(groups[1].status).toBe('error');
  });

  it('uses the same filters for raw and parsed output', () => {
    const groups = parseActivityLog([
      line('* Read prompt.md'),
      line('  | prompt.md'),
      line('* Edit'),
      line('  | Edit frontend/src/app/components/job-detail.ts')
    ]);
    const filters = { ...defaultActivityLogFilters, read: false };
    const visible = filterActivityGroups(groups, filters);

    expect(visible.map((group) => group.kind)).toEqual(['edit']);
    expect(flattenActivityLines(visible).map((entry) => entry.text)).toEqual([
      '* Edit',
      '  | Edit frontend/src/app/components/job-detail.ts'
    ]);
  });
});

function line(text: string, stream = 'stdout'): CliOutputLine {
  return {
    timestamp: '2026-04-26T12:00:00.000Z',
    stream,
    text
  };
}
