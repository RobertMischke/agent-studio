import { describe, expect, it } from 'vitest';
import {
  binToolBurstByKind,
  buildChatMessages,
  buildConversationTurns,
  defaultActivityLogFilters,
  filterActivityGroups,
  flattenActivityLines,
  formatBurstDuration,
  parseActivityLog,
  summarizeToolBurst
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
    expect(groups[0].title).toBe('Reading files ×3');
    expect(groups[0].collapsedByDefault).toBe(true);
    expect(groups[0].lines).toHaveLength(6);
  });

  it('compresses adjacent edit and command bursts so trace view stays readable', () => {
    // The trace view used to show every Edit / Run as its own row; long
    // refactor sessions made it a wall of repeated entries that drowned out
    // the substantive output. All tool kinds now collapse the same way.
    const groups = parseActivityLog([
      line('* Edit src/a.ts'),
      line('  | a.ts'),
      line('* Edit src/b.ts'),
      line('  | b.ts'),
      line('* Run npm test (shell)'),
      line('  | running tests'),
      line('* Run npm run lint (shell)'),
      line('  | linting')
    ]);

    expect(groups.map((g) => g.kind)).toEqual(['edit', 'command']);
    expect(groups[0].title).toBe('Edits ×2');
    expect(groups[1].title).toBe('Commands ×2');
    expect(groups[0].collapsedByDefault).toBe(true);
    expect(groups[1].collapsedByDefault).toBe(true);
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
  it('treats [user] stream lines as their own message group, never folded into adjacent agent output', () => {
    const groups = parseActivityLog([
      line('* Read prompt.md'),
      line('  | prompt.md'),
      line('please switch to dark mode', 'user'),
      line('* Edit', 'stdout'),
      line('  | Edit src/styles.css')
    ]);

    // The user line must be its own group sandwiched between the read and the edit.
    const kinds = groups.map(g => g.kind);
    expect(kinds).toEqual(['read', 'message', 'edit']);
    expect(groups[1].lines).toHaveLength(1);
    expect(groups[1].lines[0].stream).toBe('user');
    expect(groups[1].title).toBe('please switch to dark mode');
  });

  it('buildChatMessages assigns role="user" with author "You" for [user]-stream lines', () => {
    const groups = parseActivityLog([
      line('please switch to dark mode', 'user')
    ]);
    const messages = buildChatMessages(groups);

    expect(messages).toHaveLength(1);
    expect(messages[0].role).toBe('user');
    expect(messages[0].author).toBe('You');
    expect(messages[0].title).toBe('please switch to dark mode');
  });

  it('keeps [orchestrator] stream lines as their own group with role "orchestrator"', () => {
    const groups = parseActivityLog([
      line('* Read prompt.md'),
      line('  | prompt.md'),
      line('[reissue] Session was lost and the agent exited without acting on your follow-up.', 'orchestrator'),
      line('* Edit', 'stdout'),
      line('  | Edit src/styles.css')
    ]);

    const kinds = groups.map(g => g.kind);
    expect(kinds).toContain('orchestrator');
    const orchestrator = groups.find(g => g.kind === 'orchestrator');
    expect(orchestrator?.lines[0].stream).toBe('orchestrator');

    const messages = buildChatMessages(groups);
    const orchMsg = messages.find(m => m.role === 'orchestrator');
    expect(orchMsg).toBeDefined();
    expect(orchMsg?.author).toBe('Orchestrator');
  });
});

describe('buildConversationTurns', () => {
  it('groups consecutive tool actions into a single tool burst with counts', () => {
    const groups = parseActivityLog([
      line('* Read prompt.md'),
      line('  | prompt.md'),
      line('* Read status.md'),
      line('  | status.md'),
      line('* Read job.json'),
      line('  | job.json'),
      line('Looks good — fix is small.'),
      line('Will adjust spacing.')
    ]);
    const turns = buildConversationTurns(groups);

    // The 3 reads compress into one batch group, then the agent text becomes
    // its own turn. Result: 2 turns in alternation (tools, agent).
    expect(turns.map((t) => t.kind)).toEqual(['tools', 'agent']);
    expect(turns[0].toolSummary?.total).toBeGreaterThanOrEqual(3);
    expect(turns[0].toolSummary?.counts.read).toBeGreaterThanOrEqual(3);
    expect(turns[1].text).toContain('Looks good');
    expect(turns[1].text).toContain('Will adjust spacing');
  });

  it('keeps user messages as their own turn between agent runs', () => {
    const groups = parseActivityLog([
      line('* Read prompt.md'),
      line('  | prompt.md'),
      line('please continue', 'user'),
      line('Done — committed.', 'stdout')
    ]);
    const turns = buildConversationTurns(groups);

    expect(turns.map((t) => t.kind)).toEqual(['tools', 'user', 'agent']);
    expect(turns[1].text).toBe('please continue');
    expect(turns[2].text).toContain('Done');
  });

  it('filters [taskboard] runtime markers out of the Conversation view', () => {
    const groups = parseActivityLog([
      line('[taskboard] Started claude CLI (PID 1234), model=claude-opus-4-7', 'system'),
      line('Hello, working on it now.', 'stdout'),
      line('[taskboard] claude CLI exited: status=completed, exitCode=0, duration=12,3s', 'system')
    ]);
    const turns = buildConversationTurns(groups);

    // The two [taskboard] system markers must not produce conversation
    // turns; only the agent reply does. They still live in the raw
    // groups for the Trace view.
    expect(turns).toHaveLength(1);
    expect(turns[0].kind).toBe('agent');
    expect(turns[0].text).toContain('Hello, working on it now.');
  });

  it('treats unattached errors as system turns so they are not buried', () => {
    const groups = parseActivityLog([
      line('Build started.'),
      line('x Some failure', 'stderr'),
      line('Recovered.', 'stdout')
    ]);
    const turns = buildConversationTurns(groups);

    expect(turns.map((t) => t.kind)).toContain('system');
    const sys = turns.find((t) => t.kind === 'system');
    expect(sys?.status).toBe('error');
  });
});

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

function line(text: string, stream = 'stdout', timestamp = '2026-04-26T12:00:00.000Z'): CliOutputLine {
  return {
    timestamp,
    stream,
    text
  };
}
