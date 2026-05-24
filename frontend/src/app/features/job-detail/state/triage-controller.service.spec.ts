import { describe, expect, it, vi, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TriageController } from './triage-controller.service';
import { JobSelectionService } from './job-selection.service';
import type { JobInfo, JobDetail } from '../../../models/job.model';

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
  let selection: JobSelectionService;
  let closedDetail: boolean;

  const makeJob = (id: string, state: string): JobInfo =>
    ({
      id,
      jobKey: `wp::${id}`,
      title: id,
      state,
      order: 1,
      watchPath: '/wp',
      projectName: 'p',
    }) as unknown as JobInfo;

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
    selection = TestBed.inject(JobSelectionService);

    vi.spyOn(selection, 'closeDetail').mockImplementation(() => {
      closedDetail = true;
    });
    vi.spyOn(selection, 'showTriageToast').mockImplementation(() => {});
  });

  it('closes the panel on an internal (user-initiated) lane-clear', () => {
    const job = makeJob('task-a', '4-auto-review');
    selection.triageLaneState = '4-auto-review';

    ctrl.advanceToNextInLane('4-auto-review', job.jobKey, [job], false);

    expect(closedDetail).toBe(true);
  });

  it('does NOT close the panel on an external move when the lane is empty', () => {
    const job = makeJob('task-a', '5-human-review');
    selection.triageLaneState = '4-auto-review';
    (selection as any).selected = signal<JobDetail | null>({
      info: job,
    } as unknown as JobDetail);

    ctrl.advanceToNextInLane('4-auto-review', job.jobKey, [job], true);

    expect(closedDetail).toBe(false);
    expect(selection.triageLaneState).toBe('5-human-review');
  });
});
