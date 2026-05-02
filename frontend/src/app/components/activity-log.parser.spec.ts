import { describe, expect, it } from 'vitest';
import {
  buildChatMessages,
  buildConversationTurns,
  defaultActivityLogFilters,
  filterActivityGroups,
  flattenActivityLines,
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
    // "Reading files (3)"; the summary must recover the original count of 3.
    const summary = summarizeToolBurst(groups);
    expect(summary.total).toBe(3);
    expect(summary.counts.read).toBe(3);
  });
});

function line(text: string, stream = 'stdout'): CliOutputLine {
  return {
    timestamp: '2026-04-26T12:00:00.000Z',
    stream,
    text
  };
}
