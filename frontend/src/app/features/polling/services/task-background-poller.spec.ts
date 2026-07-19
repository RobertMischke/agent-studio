import { Subject, of, type Observable } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TaskInfo } from '../../../models/task.model';
import { TaskState } from '../../../models/task.model';
import { TaskBackgroundPoller } from './task-background-poller';

class TestPoller extends TaskBackgroundPoller<number> {
  protected readonly intervalMs = 1_000;

  readonly applied: number[] = [];
  fetchCount = 0;
  source: Observable<number> = of(1);
  clearCount = 0;

  protected fetch(): Observable<number> {
    this.fetchCount++;
    return this.source;
  }

  protected applyResponse(value: number): void {
    this.applied.push(value);
  }

  protected clearValue(): void {
    this.clearCount++;
  }
}

function task(state: string, executionStatus?: string): TaskInfo {
  return {
    id: 'AGT-1',
    watchPath: 'C:/tasks',
    state,
    execution: executionStatus ? { status: executionStatus } : undefined,
  } as TaskInfo;
}

describe('TaskBackgroundPoller', () => {
  beforeEach(() => vi.useFakeTimers());

  afterEach(() => {
    vi.useRealTimers();
  });

  it('loads an inactive task once without arming recurring polling', () => {
    const poller = new TestPoller();

    poller.syncTo(task(TaskState.HumanReview));
    vi.advanceTimersByTime(10_000);

    expect(poller.fetchCount).toBe(1);
    expect(poller.applied).toEqual([1]);
    poller.ngOnDestroy();
  });

  it('keeps polling tasks in an active processing state', () => {
    const poller = new TestPoller();

    poller.syncTo(task(TaskState.Progress));
    vi.advanceTimersByTime(2_100);

    expect(poller.fetchCount).toBe(3);
    poller.ngOnDestroy();
  });

  it('does not overlap requests when a response takes longer than the interval', () => {
    const poller = new TestPoller();
    const response = new Subject<number>();
    poller.source = response;

    poller.syncTo(task(TaskState.AutoReview));
    vi.advanceTimersByTime(3_100);
    expect(poller.fetchCount).toBe(1);

    response.next(7);
    response.complete();
    vi.advanceTimersByTime(1_100);

    expect(poller.fetchCount).toBe(2);
    expect(poller.applied).toEqual([7]);
    poller.ngOnDestroy();
  });

  it('stops recurring polling when the same task reaches an inactive state', () => {
    const poller = new TestPoller();

    poller.syncTo(task(TaskState.Progress, 'running'));
    poller.syncTo(task(TaskState.HumanReview));
    vi.advanceTimersByTime(5_000);

    expect(poller.fetchCount).toBe(2);
    poller.ngOnDestroy();
  });
});
