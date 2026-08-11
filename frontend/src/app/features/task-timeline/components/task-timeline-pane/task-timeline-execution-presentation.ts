import type { RunRecord } from '../../../run-timeline';
import type { TaskTimelineEvent } from '../../models/task-timeline.model';
import { executionContextDisclosure } from '../task-timeline-presentation';

export function timelineExecutionPresentation(event: TaskTimelineEvent, runs: readonly RunRecord[]) {
  const run = nearestRun(event, runs);
  const model = event.details?.['model']?.trim()
    || run?.executionContext?.model?.trim()
    || event.summary.match(/\bmodel\s+([^,\s]+)/i)?.[1]
    || null;
  const thinking = event.details?.['thinkingLevel']?.trim()
    || run?.executionContext?.thinkingLevel?.trim()
    || null;
  return {
    facts: [
      ...(model ? [{ label: 'Model', value: model }] : []),
      ...(thinking ? [{ label: 'Thinking', value: thinking }] : []),
    ],
    sources: executionContextDisclosure(event, run?.executionContext?.sources ?? []),
  };
}

function nearestRun(event: TaskTimelineEvent, runs: readonly RunRecord[]): RunRecord | null {
  const candidates = runs.filter(run => !!run.executionContext);
  if (candidates.length === 0) return null;
  if (event.runId) {
    const exact = candidates.find(run =>
      run.inputSessionId === event.runId || run.capturedSessionId === event.runId);
    if (exact) return exact;
  }
  const eventTime = new Date(event.ts).getTime();
  if (Number.isNaN(eventTime)) return candidates.at(-1) ?? null;
  return [...candidates].sort((left, right) =>
    distanceFrom(left, eventTime) - distanceFrom(right, eventTime))[0] ?? null;
}

function distanceFrom(run: RunRecord, eventTime: number): number {
  const runTime = new Date(run.endedAt ?? run.startedAt).getTime();
  return Number.isNaN(runTime) ? Number.MAX_SAFE_INTEGER : Math.abs(eventTime - runTime);
}
