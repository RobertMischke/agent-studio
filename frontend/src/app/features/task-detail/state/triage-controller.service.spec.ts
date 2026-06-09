import { describe, expect, it, vi, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { Subject, of, throwError } from 'rxjs';
import { TriageController } from './triage-controller.service';
import { TaskSelectionService } from './task-selection.service';
import { TaskDetailPrefetchService } from './task-detail-prefetch.service';
import { LanePagerService } from './lane-pager.service';
import { TaskService } from '../../../services/task.service';
import { ErrorDialogService } from '../../../services/error-dialog.service';
import type { TaskInfo, TaskDetail } from '../../../models/task.model';

const noop = (): void => undefined;

/**
 * Regression for orchestrator-decision-closing-task:
 *
 * When the orchestrator moves a task from one lane to another (e.g.
 * 4-auto-review -> 5-human-review), the auto-advance effect fires
 * because the selected job's state diverges from triageLaneState.
 * If no peers remain in the original lane, the old code called
 * closeDetail() and the task panel closed from under the user.
 *
 * The fix: when `external === true` and no candidate peer exists,
 * follow the job to its new state instead of closing the panel.
 */
describe('TriageController · advanceToNextInLane', () => {
  let ctrl: TriageController;
  let selection: TaskSelectionService;
  let closedDetail: boolean;

  const makeJob = (id: string, state: string): TaskInfo =>
    ({
      id,
      taskKey: `wp::${id}`,
      title: id,
      state,
      order: 1,
      watchPath: '/wp',
      projectName: 'p',
    }) as unknown as TaskInfo;

  beforeEach(async () => {
    closedDetail = false;

    await TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    ctrl = TestBed.inject(TriageController);
    selection = TestBed.inject(TaskSelectionService);

    vi.spyOn(selection, 'closeDetail').mockImplementation(() => {
      closedDetail = true;
    });
    vi.spyOn(selection, 'showTriageToast').mockImplementation(noop);
  });

  it('closes the panel on an internal (user-initiated) lane-clear', () => {
    const job = makeJob('task-a', '4-auto-review');
    selection.triageLaneState = '4-auto-review';

    ctrl.advanceToNextInLane('4-auto-review', job.taskKey, [job], false);

    expect(closedDetail).toBe(true);
  });

  it('does NOT close the panel on an external move when the lane is empty', () => {
    const job = makeJob('task-a', '5-human-review');
    selection.triageLaneState = '4-auto-review';
    (selection as unknown as { selected: ReturnType<typeof signal<TaskDetail | null>> }).selected = signal<TaskDetail | null>({
      info: job,
    } as unknown as TaskDetail);

    ctrl.advanceToNextInLane('4-auto-review', job.taskKey, [job], true);

    expect(closedDetail).toBe(false);
    expect(selection.triageLaneState).toBe('5-human-review');
  });
});

/**
 * The "accept-to-next-task feels instant" regression: when the user
 * clicks Mark-as-Done on Job A, the next peer must render before the
 * move POST returns. The previous shape awaited the POST first, so the
 * user paid the POST roundtrip plus a getDetail roundtrip in series.
 *
 * The two assertions below pin the new contract:
 *   1. The panel advances synchronously - by the time `move(...)`
 *      returns, the prefetched detail for the next peer is in
 *      `selected`, before any `next` callback on the POST observable
 *      fires.
 *   2. When the POST eventually errors, the optimistic move is
 *      reverted and the panel navigates back to the original job, so
 *      the user does not silently lose their click.
 */
describe('TriageController · optimistic navigation on Accept', () => {
  let ctrl: TriageController;
  let selection: TaskSelectionService;
  let prefetch: TaskDetailPrefetchService;
  let jobService: TaskService;

  const wp = '/wp';
  const makeJob = (id: string, state: string): TaskInfo =>
    ({
      id,
      taskKey: `${wp}::${id}`,
      title: id,
      state,
      order: 1,
      watchPath: wp,
      projectName: 'p',
    }) as unknown as TaskInfo;

  const makeDetail = (id: string, state: string): TaskDetail =>
    ({
      info: makeJob(id, state),
      promptMarkdown: null,
      promptHistory: [],
      titleHistory: [],
      statusMarkdown: null,
      contextUsage: null,
      log: [],
      summaryState: null,
      reviewEvidence: [],
    }) as unknown as TaskDetail;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    ctrl = TestBed.inject(TriageController);
    selection = TestBed.inject(TaskSelectionService);
    prefetch = TestBed.inject(TaskDetailPrefetchService);
    jobService = TestBed.inject(TaskService);
    prefetch.clear();
  });

  it('advances synchronously to the prefetched next peer while the move POST is still in flight', () => {
    const taskA = makeJob('task-a', '5-human-review');
    const taskB = makeJob('task-b', '5-human-review');
    const taskBDetail = makeDetail('task-b', '5-human-review');

    // Seed the lane-pager snapshot for [A, B], anchored on A. The
    // service's effect prefetches B on snapshot change, so we mock the
    // GET to land deterministically before move() runs.
    const detailSpy = vi.spyOn(jobService, 'getDetail').mockReturnValue(of(taskBDetail));
    TestBed.inject(LanePagerService).capture('5-human-review', [taskA, taskB], taskA.taskKey);
    selection.triageLaneState = '5-human-review';
    (selection as unknown as { selected: ReturnType<typeof signal<TaskDetail | null>> }).selected
      = signal<TaskDetail | null>(makeDetail('task-a', '5-human-review'));

    // POST never resolves so we can assert nav happened before completion.
    const movePost = new Subject<object>();
    vi.spyOn(jobService, 'moveJob').mockReturnValue(movePost.asObservable());
    vi.spyOn(jobService, 'applyOptimisticMove').mockReturnValue({} as never);

    ctrl.move(taskA, { targetState: '6-completed', actionId: 'mark-done' });
    const observedSelectedDuringCall: TaskDetail | null = selection.selected();

    expect(observedSelectedDuringCall?.info.id).toBe('task-b');
    // POST is still on the wire — no `next` callback has fired yet.
    expect(detailSpy).toHaveBeenCalled();
    movePost.complete();
  });

  it('reverts the optimistic move and navigates back when the POST errors', () => {
    const taskA = makeJob('task-a', '5-human-review');
    const taskB = makeJob('task-b', '5-human-review');
    const taskADetail = makeDetail('task-a', '5-human-review');
    const taskBDetail = makeDetail('task-b', '5-human-review');

    vi.spyOn(jobService, 'getDetail').mockImplementation((id: string) => {
      return of(id === 'task-a' ? taskADetail : taskBDetail);
    });
    TestBed.inject(LanePagerService).capture('5-human-review', [taskA, taskB], taskA.taskKey);
    selection.triageLaneState = '5-human-review';
    (selection as unknown as { selected: ReturnType<typeof signal<TaskDetail | null>> }).selected
      = signal<TaskDetail | null>(taskADetail);

    const revertSpy = vi.spyOn(jobService, 'revertOptimisticMove').mockImplementation(noop);
    vi.spyOn(jobService, 'applyOptimisticMove').mockReturnValue(
      { fromLane: 'humanReview', before: [], toLane: 'completed', toBefore: [] } as never,
    );
    vi.spyOn(jobService, 'moveJob').mockReturnValue(
      throwError(() => ({ status: 500, message: 'boom' })),
    );

    // Suppress the error dialog so the test does not pop a modal.
    const errorDialog = TestBed.inject(ErrorDialogService);
    vi.spyOn(errorDialog, 'show').mockImplementation(noop);

    const openSpy = vi.spyOn(selection, 'openDetail').mockImplementation(noop);

    ctrl.move(taskA, { targetState: '6-completed', actionId: 'mark-done' });

    expect(revertSpy).toHaveBeenCalled();
    expect(openSpy).toHaveBeenCalledWith(taskA);
  });
});
