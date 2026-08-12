import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { TaskInfo } from '../../../models/task.model';
import { TaskService } from '../../../services/task.service';
import { JobsHubClient } from '../../../services/jobs-hub-client.service';
import { PlanPollService } from './plan-poll.service';

describe('PlanPollService SignalR convergence', () => {
  it('refreshes the selected plan immediately for a matching planUpdated push', () => {
    const getPlan = vi.fn(() => of(null));
    const hub = { planUpdatedEvent: signal<{ jobId: string; cliType: string } | null>(null) };
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        PlanPollService,
        { provide: TaskService, useValue: { getPlan } },
        { provide: JobsHubClient, useValue: hub },
      ],
    });
    const service = TestBed.inject(PlanPollService);
    service.syncTo({ id: 'AGT-2641', watchPath: '/workspace', state: '3-progress' } as TaskInfo);
    expect(getPlan).toHaveBeenCalledTimes(1);

    hub.planUpdatedEvent.set({ jobId: 'AGT-2641', cliType: 'codex' });
    TestBed.flushEffects();

    expect(getPlan).toHaveBeenCalledTimes(2);
    service.stop();
  });
});
