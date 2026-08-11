import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { JobsHubClient, type PlanUpdatedEvent } from '../../../../services/jobs-hub-client.service';
import { TaskService } from '../../../../services/task.service';
import type { TaskPlanView } from '../../../plan-strip';
import { OrchestratorTaskProgressComponent } from './orchestrator-task-progress.component';

function plan(done: boolean): TaskPlanView {
  return {
    hasPlan: true,
    source: 'codex/todo_list',
    snapshotCount: done ? 2 : 1,
    activeItemId: done ? null : 'render',
    softEstimateMedian: null,
    unassignedSubActions: [],
    items: [
      {
        id: 'render',
        title: 'Render live progress',
        status: done ? 'done' : 'active',
        subActionCount: 0,
        subActions: [],
      },
    ],
  };
}

describe('OrchestratorTaskProgressComponent', () => {
  it('refreshes the visible task plan from the SignalR planUpdated hint', async () => {
    const update = signal<PlanUpdatedEvent | null>(null);
    const getPlan = vi.fn()
      .mockReturnValueOnce(of(plan(false)))
      .mockReturnValueOnce(of(plan(true)));
    await TestBed.configureTestingModule({
      imports: [OrchestratorTaskProgressComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: { getPlan } },
        { provide: JobsHubClient, useValue: { planUpdatedEvent: update } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(OrchestratorTaskProgressComponent);
    fixture.componentRef.setInput('jobId', 'AGT-2641');
    fixture.componentRef.setInput('watchPath', '/workspace');
    fixture.componentRef.setInput('runActive', true);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(fixture.nativeElement.querySelector('[data-testid="plan-strip-count"]')?.textContent)
      .toContain('0/1 done');

    update.set({ jobId: 'AGT-2641', cliType: 'codex', sequence: 1 });
    fixture.detectChanges();
    await fixture.whenStable();

    expect(getPlan).toHaveBeenCalledTimes(2);
    expect(fixture.nativeElement.querySelector('[data-testid="plan-strip-count"]')?.textContent)
      .toContain('1/1 done');
    expect(fixture.nativeElement.querySelector('[data-testid="plan-strip"]')?.getAttribute('data-variant'))
      .toBe('context');
  });
});
