import type { CliOutputLine } from '../../../../models/task.model';
import type { TaskPlanView } from '../../../plan-strip/plan.model';
import {
  type ConversationEvent,
  type PlanItemStatus,
  type PlanUpdateEvent,
} from 'coding-agent-chat/core';

/**
 * Keep native Codex TODO_LIST JSON out of the semantic Activity stream. The
 * untouched source array continues to feed Trace; the plan snapshot endpoint
 * supplies the checklist row below.
 */
export function withoutRawCodexTodoListFrames(lines: readonly CliOutputLine[]): CliOutputLine[] {
  return lines.filter(line => !isRawCodexTodoListFrame(line.text));
}

/**
 * Collapse every projected plan snapshot into one stable event id. Angular
 * therefore keeps the checklist component and each stable item row mounted
 * while statuses change, which lets the status-change animation call out only
 * the current delta instead of stacking historical plan rows.
 */
export function withLivePlanSnapshot(
  events: readonly ConversationEvent[],
  plan: TaskPlanView | null,
  source: string,
): ConversationEvent[] {
  const priorPlans = events.filter((event): event is PlanUpdateEvent => event.kind === 'plan.update');
  const latestPrior = priorPlans.at(-1) ?? null;
  const hasCanonical = !!plan?.hasPlan && plan.items.length > 0;
  if (!hasCanonical && !latestPrior) return [...events];

  const items = hasCanonical
    ? plan!.items.map(item => ({
        id: item.id,
        title: item.title,
        status: toConversationStatus(item.status),
      }))
    : latestPrior!.items;
  const fallbackTimestamp = events.at(-1)?.timestamp ?? '';
  const live: PlanUpdateEvent = {
    ...(latestPrior ?? {}),
    id: `${source}:live-plan`,
    kind: 'plan.update',
    timestamp: plan?.updatedAt ?? latestPrior?.timestamp ?? fallbackTimestamp,
    rawRange: latestPrior?.rawRange ?? { source, start: 0, end: 0 },
    items,
  };

  return [
    ...events.filter(event => event.kind !== 'plan.update'),
    live,
  ].sort((left, right) => left.timestamp.localeCompare(right.timestamp));
}

function toConversationStatus(status: string): PlanItemStatus {
  if (status === 'done') return 'completed';
  if (status === 'active') return 'in_progress';
  return 'pending';
}

function isRawCodexTodoListFrame(text: string): boolean {
  const trimmed = text.trimStart();
  if (!trimmed.startsWith('{') || !trimmed.includes('"todo_list"')) return false;
  try {
    const frame = JSON.parse(trimmed) as {
      type?: unknown;
      item?: { type?: unknown };
    };
    return (frame.type === 'item.started' || frame.type === 'item.updated' || frame.type === 'item.completed')
      && frame.item?.type === 'todo_list';
  } catch {
    return false;
  }
}
