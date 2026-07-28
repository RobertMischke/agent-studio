import { describe, expect, it } from 'vitest';
import { TIMELINE_KIND, type TaskTimelineEvent } from '../models/task-timeline.model';
import {
  executionContextDisclosure,
  timelineDetailEntries,
  timelineEventReason,
  timelineEventSummary,
  timelineEventTitle,
  timelineKindLabel,
} from './task-timeline-presentation';

function event(overrides: Partial<TaskTimelineEvent> = {}): TaskTimelineEvent {
  return {
    ts: '2026-07-28T10:00:00Z',
    kind: TIMELINE_KIND.promptCreated,
    actor: 'system',
    summary: '',
    ...overrides,
  };
}

describe('task timeline presentation', () => {
  it('provides a quiet human label for every closed event kind', () => {
    for (const kind of Object.values(TIMELINE_KIND)) {
      const label = timelineKindLabel(kind);
      expect(label).not.toBe(kind);
      expect(label).not.toContain('_');
    }
  });

  it('folds a generated run-finish summary and status into one title', () => {
    const completed = event({
      kind: TIMELINE_KIND.agentRunFinished,
      summary: 'codex run completed after 247.6s',
      details: { cli: 'codex', status: 'completed' },
    });

    expect(timelineEventTitle(completed)).toBe('Run finished · 247.6s');
    expect(timelineEventSummary(completed)).toBeNull();
    expect(timelineDetailEntries(completed)).toEqual([]);
  });

  it('keeps a non-generated run-finish note that adds information', () => {
    const completed = event({
      kind: TIMELINE_KIND.agentRunFinished,
      summary: 'Agent reported that visual evidence could not be captured',
      details: { status: 'completed', durationSeconds: '15.2' },
    });

    expect(timelineEventTitle(completed)).toBe('Run finished · 15.2s');
    expect(timelineEventSummary(completed)).toContain('visual evidence');
  });

  it('removes defaults, zero counts, CLI duplication, and values already told in prose', () => {
    const started = event({
      kind: TIMELINE_KIND.agentRunStarted,
      summary: 'codex CLI start',
      details: {
        cli: 'codex',
        model: 'gpt-5.6-sol',
        quotaFallback: 'false',
        resumed: 'false',
        mcp: '0',
        fallbackReason: '',
        intent: 'start',
      },
    });

    expect(timelineDetailEntries(started)).toEqual([
      { key: 'model', label: 'Model', value: 'gpt-5.6-sol' },
    ]);
  });

  it('does not repeat a reason already present in the event story', () => {
    const requeued = event({
      kind: TIMELINE_KIND.operatorRequeued,
      summary: 'Operator reopened the task for fresh assessment: verify density',
      details: { reason: 'verify density' },
    });

    expect(timelineEventReason(requeued)).toBeNull();
  });

  it('turns the execution-context count into a disclosure backed by exact sources', () => {
    const context = event({
      kind: TIMELINE_KIND.executionContext,
      summary: 'codex context: 3 sources, model gpt-5.6-sol, YOLO',
      details: {
        cli: 'codex',
        source: 'convention',
        sources: '2',
        mcp: '0',
        model: 'gpt-5.6-sol',
        thinkingLevel: 'medium',
        sourceItems: JSON.stringify([
          { kind: 'memory', label: 'AGENTS.md', path: '/repo/AGENTS.md', exists: true, detail: null },
          { kind: 'global-config', label: 'Codex config', path: '/home/.codex/config.toml', exists: true, detail: null },
        ]),
      },
    });

    expect(timelineEventTitle(context)).toBe('Execution context');
    expect(timelineEventSummary(context)).toBeNull();
    expect(timelineDetailEntries(context)).toEqual([]);
    expect(executionContextDisclosure(context)).toMatchObject({
      label: '2 sources',
      origin: 'Codex config conventions',
      sources: [{ label: 'AGENTS.md' }, { label: 'Codex config' }],
    });
  });

  it('omits an unexpandable legacy source count when no source elements survive', () => {
    const context = event({
      kind: TIMELINE_KIND.executionContext,
      details: { cli: 'codex', source: 'convention', sources: '3', mcp: '0' },
    });

    expect(executionContextDisclosure(context)).toBeNull();
  });
});
