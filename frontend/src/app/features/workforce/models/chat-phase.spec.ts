import { describe, it, expect } from 'vitest';
import { groupIntoPhases, buildSummary, type PhaseInputMessage } from './chat-phase';
import { getRole } from './workforce-role';

function msg(id: string, author: string, kind = 'turn', refs: string[] = []): PhaseInputMessage {
  return {
    id,
    ts: `2026-05-11T10:${id.padStart(2, '0')}:00Z`,
    author,
    kind,
    refs,
  };
}

describe('groupIntoPhases', () => {
  it('returns no phases for an empty input', () => {
    expect(groupIntoPhases([])).toEqual([]);
  });

  it('opens a new phase at every user turn', () => {
    const phases = groupIntoPhases([
      msg('01', 'user'),
      msg('02', 'agent'),
      msg('03', 'agent'),
      msg('04', 'user'),
      msg('05', 'agent'),
    ]);
    expect(phases).toHaveLength(2);
    expect(phases[0].messageIds).toEqual(['01', '02', '03']);
    expect(phases[1].messageIds).toEqual(['04', '05']);
  });

  it('handles a leading agent block before the first user turn', () => {
    const phases = groupIntoPhases([
      msg('01', 'agent'),
      msg('02', 'agent'),
      msg('03', 'user'),
      msg('04', 'agent'),
    ]);
    expect(phases).toHaveLength(2);
    expect(phases[0].hasUser).toBe(false);
    expect(phases[1].hasUser).toBe(true);
  });

  it('lists participants in first-seen order without duplicates', () => {
    const phases = groupIntoPhases([
      msg('01', 'user'),
      msg('02', 'claude'),
      msg('03', 'claude', 'turn', ['aspect:code-quality']),
      msg('04', 'claude'),
      msg('05', 'orchestrator'),
    ]);
    const ids = phases[0].participants.map((p) => p.id);
    expect(ids).toEqual(['user', 'task-executor', 'code-reviewer', 'orchestrator']);
  });

  it('attaches a stable phase id derived from the first message', () => {
    const phases = groupIntoPhases([msg('01', 'user'), msg('02', 'agent')]);
    expect(phases[0].id).toBe('phase-01');
  });

  it('records start and end timestamps', () => {
    const phases = groupIntoPhases([msg('01', 'user'), msg('05', 'agent')]);
    expect(phases[0].startTs).toBe('2026-05-11T10:01:00Z');
    expect(phases[0].endTs).toBe('2026-05-11T10:05:00Z');
  });

  it('renders a deterministic summary line per phase', () => {
    const phases = groupIntoPhases([
      msg('01', 'user'),
      msg('02', 'claude'),
      msg('03', 'claude', 'turn', ['aspect:code-quality']),
    ]);
    expect(phases[0].summary).toContain('You steered');
    expect(phases[0].summary).toContain('Task Executor');
    expect(phases[0].summary).toContain('Code Reviewer');
  });
});

describe('buildSummary', () => {
  const exec = getRole('task-executor');
  const reviewer = getRole('code-reviewer');
  const custodian = getRole('architecture-custodian');
  const user = getRole('user');

  it('chains two participants with "and"', () => {
    expect(buildSummary([user, exec, reviewer], 3, true)).toBe(
      'You steered; Task Executor and Code Reviewer responded (3 messages).'
    );
  });

  it('chains three or more participants with commas + "then"', () => {
    expect(buildSummary([user, exec, reviewer, custodian], 5, true)).toBe(
      'You steered; Task Executor, Code Reviewer, then Architecture Custodian responded (5 messages).'
    );
  });

  it('renders a single-participant phase cleanly', () => {
    expect(buildSummary([exec], 1, false)).toBe('Task Executor responded (1 message).');
  });

  it('falls back when the workforce did not respond at all', () => {
    expect(buildSummary([user], 1, true)).toBe('You opened the conversation (1 message).');
  });
});
