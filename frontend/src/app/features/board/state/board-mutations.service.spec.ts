import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { BatchMoveJobResponse, TaskInfo } from '../../../models/task.model';
import { ConfirmDialogService } from '../../../services/confirm-dialog.service';
import { ErrorDialogService } from '../../../services/error-dialog.service';
import { NotificationService } from '../../../services/notification.service';
import { TaskService } from '../../../services/task.service';
import { UndoController } from '../../../services/undo.service';
import { TaskSelectionService } from '../../task-detail/state/task-selection.service';
import { BoardMutationsService } from './board-mutations.service';

function batch(
  status: BatchMoveJobResponse['status'],
  results: BatchMoveJobResponse['results'],
): BatchMoveJobResponse {
  const succeeded = results.filter((result) => result.status === 'moved').length;
  return {
    id: 'batch-123',
    status,
    total: 3,
    completed: results.length,
    succeeded,
    failed: results.length - succeeded,
    results,
    metrics: {
      totalDurationMs: 0,
      itemMoveDurationMs: 0,
      laneLockAcquisitions: 0,
      laneLockWaitMs: 0,
      laneLockHeldMs: 0,
      scannerInvalidations: 0,
      scannerRefreshes: 0,
      scannerRefreshMs: 0,
      gitProcesses: 0,
      gitProcessMs: 0,
    },
    message: null,
    createdAt: '2026-08-09T10:00:00Z',
    startedAt: status === 'queued' ? null : '2026-08-09T10:00:00Z',
    finishedAt: status === 'completed' ? '2026-08-09T10:00:01Z' : null,
  };
}

describe('BoardMutationsService archive job', () => {
  let service: BoardMutationsService;
  let notifications: NotificationService;
  let taskService: {
    startBatchMove: ReturnType<typeof vi.fn>;
    getBatchMove: ReturnType<typeof vi.fn>;
    applyOptimisticMove: ReturnType<typeof vi.fn>;
    refresh: ReturnType<typeof vi.fn>;
  };

  beforeEach(() => {
    vi.useFakeTimers();
    const queued = batch('queued', []);
    const first = batch('running', [
      { index: 0, jobId: 'alpha', status: 'moved', message: null, durationMs: 10 },
    ]);
    const finished = batch('completed', [
      ...first.results,
      { index: 1, jobId: 'beta', status: 'conflict', message: 'target exists', durationMs: 12 },
      { index: 2, jobId: 'gamma', status: 'moved', message: null, durationMs: 11 },
    ]);
    taskService = {
      startBatchMove: vi.fn(() => of(queued)),
      getBatchMove: vi.fn()
        .mockReturnValueOnce(of(first))
        .mockReturnValueOnce(of(finished)),
      applyOptimisticMove: vi.fn(),
      refresh: vi.fn(),
    };

    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        BoardMutationsService,
        NotificationService,
        { provide: TaskService, useValue: taskService },
        { provide: ErrorDialogService, useValue: { show: vi.fn() } },
        { provide: ConfirmDialogService, useValue: { confirm: vi.fn() } },
        { provide: TaskSelectionService, useValue: { selected: signal(null) } },
        { provide: UndoController, useValue: { offerLaneRevert: vi.fn(), cancelActive: vi.fn() } },
      ],
    });
    service = TestBed.inject(BoardMutationsService);
    notifications = TestBed.inject(NotificationService);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('submits once, paints progress incrementally, and reports each partial failure', async () => {
    const tasks = [
      { id: 'alpha', title: 'Alpha', watchPath: '/workspace' },
      { id: 'beta', title: 'Beta', watchPath: '/workspace' },
      { id: 'gamma', title: 'Gamma', watchPath: '/workspace' },
    ] as TaskInfo[];

    service.archiveAllCompleted(tasks);

    expect(taskService.startBatchMove).toHaveBeenCalledOnce();
    expect(service.archiving()).toBe(true);
    expect(notifications.notifications()[0].message).toBe('Archiving 0 of 3 tasks...');

    await vi.advanceTimersByTimeAsync(1);
    expect(service.archiveProgress()?.completed).toBe(1);
    expect(taskService.applyOptimisticMove).toHaveBeenCalledWith('alpha', '/workspace', '7-archive');
    expect(notifications.notifications()[0].message).toBe('Archiving 1 of 3 tasks...');

    await vi.advanceTimersByTimeAsync(250);
    expect(service.archiving()).toBe(false);
    expect(taskService.applyOptimisticMove).toHaveBeenCalledTimes(2);
    expect(taskService.applyOptimisticMove).toHaveBeenLastCalledWith('gamma', '/workspace', '7-archive');
    expect(taskService.refresh).toHaveBeenCalledWith(true);

    const warning = notifications.notifications()[0];
    expect(warning.kind).toBe('warning');
    expect(warning.message).toBe('Archived 2 of 3 tasks. 1 needs attention.');
    expect(warning.details).toEqual(['beta: target exists']);
  });
});
