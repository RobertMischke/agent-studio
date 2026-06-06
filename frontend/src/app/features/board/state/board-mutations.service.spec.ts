import { describe, expect, it, vi, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { BoardMutationsService } from './board-mutations.service';
import { TaskService } from '../../../services/task.service';
import { TaskSelectionService } from '../../task-detail';
import type { TaskInfo } from '../../../models/task.model';

/**
 * Regression for the backlog-triage navigation bug: promoting a task
 * from the standalone triage screen used to call `changeStateFromDetail`,
 * which sets the detail/pager context and yanks the user into the
 * task-detail view. `changeStateFromTriage` must move the task without
 * ever touching `TaskSelectionService`, so the user stays in the triage
 * list while the moved card drops out of it.
 */
describe('BoardMutationsService · changeStateFromTriage', () => {
  let service: BoardMutationsService;
  let jobService: TaskService;
  let selection: TaskSelectionService;

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

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    service = TestBed.inject(BoardMutationsService);
    jobService = TestBed.inject(TaskService);
    selection = TestBed.inject(TaskSelectionService);
  });

  const noop = (): void => undefined;

  it('moves the task without selecting it or advancing the pager', () => {
    const task = makeJob('task-a', '0-backlog');

    vi.spyOn(jobService, 'findLaneIndex').mockReturnValue(0);
    vi.spyOn(jobService, 'applyOptimisticMove').mockReturnValue({} as never);
    vi.spyOn(jobService, 'beginOptimisticPersist').mockImplementation(noop);
    vi.spyOn(jobService, 'endOptimisticPersist').mockImplementation(noop);
    const moveSpy = vi.spyOn(jobService, 'moveJob').mockReturnValue(of({} as object));
    const detailSpy = vi.spyOn(jobService, 'getDetail');
    const advanceSpy = vi.spyOn(selection, 'advanceAfterMutation');

    service.changeStateFromTriage(task, '1-preparation');

    expect(moveSpy).toHaveBeenCalledWith('task-a', '1-preparation', wp);
    // The detail view must not be touched: no pager advance, no detail
    // fetch, and nothing selected.
    expect(advanceSpy).not.toHaveBeenCalled();
    expect(detailSpy).not.toHaveBeenCalled();
    expect(selection.selected()).toBeNull();
  });

  it('is a no-op when the target equals the current state', () => {
    const task = makeJob('task-a', '0-backlog');
    const moveSpy = vi.spyOn(jobService, 'moveJob');

    service.changeStateFromTriage(task, '0-backlog');

    expect(moveSpy).not.toHaveBeenCalled();
  });
});
