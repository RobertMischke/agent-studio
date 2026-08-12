import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { TaskPlanView } from '../../../plan-strip/plan.model';
import { OrchestratorTaskProgressComponent } from './orchestrator-task-progress.component';

describe('OrchestratorTaskProgressComponent', () => {
  it('renders a compact stable checklist with progress and all three statuses', async () => {
    await TestBed.configureTestingModule({
      imports: [OrchestratorTaskProgressComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorTaskProgressComponent);
    const plan: TaskPlanView = {
      hasPlan: true,
      source: 'codex/todo_list',
      snapshotCount: 2,
      activeItemId: 'integrate',
      softEstimateMedian: null,
      items: [
        { id: 'inspect', title: 'Inspect Activity', status: 'done', subActionCount: 0, subActions: [] },
        { id: 'integrate', title: 'Integrate progress', status: 'active', subActionCount: 0, subActions: [] },
        { id: 'verify', title: 'Run tests', status: 'pending', subActionCount: 0, subActions: [] },
      ],
      unassignedSubActions: [],
    };
    fixture.componentRef.setInput('plan', plan);
    fixture.componentRef.setInput('isRunning', true);
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="orchestrator-task-progress-count"]')?.textContent).toContain('1/3 complete');
    expect(element.querySelectorAll('[data-testid="orchestrator-task-progress-item"]')).toHaveLength(3);
    expect(Array.from(element.querySelectorAll('[data-status]')).map(item => item.getAttribute('data-status')))
      .toEqual(['done', 'active', 'pending']);
    expect(element.textContent).toContain('Live');
  });
});
