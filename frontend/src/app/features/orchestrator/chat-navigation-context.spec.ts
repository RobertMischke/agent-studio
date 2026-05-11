import { describe, expect, it } from 'vitest';
import { buildChatNavigationContext } from './chat-navigation-context';

/**
 * Pure-function unit test for the navigation-context builder that ships
 * on every project-chat POST. The shape is part of the chat agent's
 * contract: missing or stale fields produced the 2026-05-09 "Conversation,
 * Foul Conversation" hallucination. This locks the routing rules so future
 * router refactors cannot silently strip the context.
 */
describe('buildChatNavigationContext', () => {
  const fixedNow = () => new Date('2026-05-09T08:42:00.000Z');

  it('marks task-detail when a job id is active and forwards id + title', () => {
    const ctx = buildChatNavigationContext({
      activeJobId: 'bug-auto-review-reorder-drops-card',
      activeJobTitle: 'Bug: reordering a card inside auto-review drops it from the lane',
      now: fixedNow
    });

    expect(ctx.currentPage).toBe('task-detail');
    expect(ctx.currentTaskId).toBe('bug-auto-review-reorder-drops-card');
    expect(ctx.currentTaskTitle).toBe(
      'Bug: reordering a card inside auto-review drops it from the lane'
    );
    expect(ctx.viewportTimestamp).toBe('2026-05-09T08:42:00.000Z');
  });

  it('forwards optional task state and lane filter when supplied', () => {
    const ctx = buildChatNavigationContext({
      activeJobId: 'bug-X',
      activeJobTitle: 'Bug X',
      activeJobState: '4-auto-review',
      laneFilter: '4-auto-review',
      now: fixedNow
    });
    expect(ctx.currentTaskState).toBe('4-auto-review');
    expect(ctx.currentLaneFilter).toBe('4-auto-review');
  });

  it('defaults to kanban-board when no task is active and omits task fields', () => {
    const ctx = buildChatNavigationContext({
      activeJobId: null,
      activeJobTitle: null,
      now: fixedNow
    });
    expect(ctx.currentPage).toBe('kanban-board');
    expect(ctx.currentTaskId).toBeUndefined();
    expect(ctx.currentTaskTitle).toBeUndefined();
    expect(ctx.viewportTimestamp).toBe('2026-05-09T08:42:00.000Z');
  });

  it('treats whitespace-only ids as absent', () => {
    const ctx = buildChatNavigationContext({
      activeJobId: '   ',
      activeJobTitle: '',
      now: fixedNow
    });
    expect(ctx.currentPage).toBe('kanban-board');
    expect(ctx.currentTaskId).toBeUndefined();
    expect(ctx.currentTaskTitle).toBeUndefined();
  });

  it('stamps the current wall-clock viewport timestamp when no now() override is given', () => {
    const ctx = buildChatNavigationContext({
      activeJobId: 'bug-X',
      activeJobTitle: 'Bug X'
    });
    expect(typeof ctx.viewportTimestamp).toBe('string');
    expect(ctx.viewportTimestamp).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}/);
  });
});
