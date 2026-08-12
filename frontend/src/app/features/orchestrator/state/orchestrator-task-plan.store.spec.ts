import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { JobsHubClient } from '../../../services/jobs-hub-client.service';
import { TaskService } from '../../../services/task.service';
import type { TaskPlanView } from '../../plan-strip';
import { OrchestratorTaskPlanStore } from './orchestrator-task-plan.store';

const firstPlan: TaskPlanView = {
  hasPlan: true,
  source: 'codex/todo_list',
  snapshotCount: 1,
  activeItemId: 'one',
  softEstimateMedian: null,
  items: [{ id: 'one', title: 'Inspect', status: 'active', subActionCount: 0, subActions: [] }],
  unassignedSubActions: [],
};

describe('OrchestratorTaskPlanStore', () => {
  it('loads task context immediately and refetches it on the matching SignalR update', () => {
    const getPlan = vi.fn().mockReturnValue(of(firstPlan));
    const hub = { planUpdated: signal<{ jobId: string; cliType: string; revision: number } | null>(null) };
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        OrchestratorTaskPlanStore,
        { provide: TaskService, useValue: { getPlan } },
        { provide: JobsHubClient, useValue: hub },
      ],
    });
    const store = TestBed.inject(OrchestratorTaskPlanStore);

    store.select('job-1', '/workspace');
    TestBed.tick();
    expect(store.plan()).toEqual(firstPlan);
    expect(getPlan).toHaveBeenCalledTimes(1);

    hub.planUpdated.set({ jobId: 'job-1', cliType: 'codex', revision: 1 });
    TestBed.tick();
    expect(getPlan).toHaveBeenCalledTimes(2);

    hub.planUpdated.set({ jobId: 'another-job', cliType: 'codex', revision: 2 });
    TestBed.tick();
    expect(getPlan).toHaveBeenCalledTimes(2);
  });
});
