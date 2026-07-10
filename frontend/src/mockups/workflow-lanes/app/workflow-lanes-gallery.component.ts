import { ChangeDetectionStrategy, Component } from '@angular/core';
import { of } from 'rxjs';

import { ProjectWorkflowSectionComponent } from '../../../app/features/project-detail';
import { TaskService } from '../../../app/services/task.service';

/**
 * T6a backend-free visual harness for the Workflow / Lanes page (stage 1).
 *
 * Renders the shipped `ProjectWorkflowSectionComponent` verbatim, driven by a
 * stub `TaskService` that returns the same shapes the real REST endpoints do.
 * No backend, HTTP, or SignalR — the same precedent as the other src/mockups/*
 * harnesses — so a real-browser screenshot of the read-only transparency
 * surface can be captured without standing up a platform backend (which would
 * side-effect shared workspace state).
 *
 * The seeded settings mirror a realistic project: auto-commit on, push on
 * completed, two enabled gate steps, and a resolved per-lane sort map, so the
 * live transition pills and the relocated sort controls render with real
 * product copy rather than the loading dash.
 */
const STUB: Partial<TaskService> = {
  getLaneSortStrategies: () =>
    of({
      resolved: {
        '0-backlog': 'oldest-first',
        '1-preparation': 'lane-entry',
        '2-ready': 'oldest-first',
        '3-progress': 'newest-first',
        '4-auto-review': 'last-activity',
        '5-human-review': 'last-activity',
        '5e-escalated': 'newest-first',
        '6-completed': 'last-activity',
        '7-archive': 'newest-first',
      },
      overrides: { '3-progress': 'newest-first' },
      available: ['lane-entry', 'manual', 'newest-first', 'oldest-first', 'last-activity'],
    }),
  setLaneSortStrategy: (_p: string, lane: string, strategy: string) =>
    of({ lane, strategy, override: strategy }),
  getProjectSnapshot: () =>
    of({ settings: { autoCommit: true, autoPushStrategy: 'on-completed' } }) as unknown as ReturnType<
      TaskService['getProjectSnapshot']
    >,
  getPipelineCatalogue: () =>
    of({
      pipelineId: 'default',
      steps: [
        {
          id: 'aspect-requirement-fit',
          displayName: 'Requirement fit',
          kind: 'aspect',
          usesModel: true,
          usesPrompt: true,
          supportsMode: true,
          canDisable: true,
          defaultEnabled: true,
          supportsCondition: false,
        },
        {
          id: 'code-review-grade',
          displayName: 'Code-review grade',
          kind: 'review',
          usesModel: true,
          usesPrompt: true,
          supportsMode: true,
          canDisable: true,
          defaultEnabled: true,
          supportsCondition: false,
        },
        {
          id: 'auto-commit',
          displayName: 'Auto commit',
          kind: 'core',
          usesModel: false,
          usesPrompt: false,
          supportsMode: false,
          canDisable: false,
          defaultEnabled: true,
          supportsCondition: false,
        },
      ],
    }) as unknown as ReturnType<TaskService['getPipelineCatalogue']>,
  getAllProjectSettings: () =>
    of({
      'Agent Task Processor': {
        autoCommit: true,
        autoPushStrategy: 'on-completed',
        runnerMode: null,
        orchestratorModel: null,
        pipelineSteps: {},
      },
    }) as unknown as ReturnType<TaskService['getAllProjectSettings']>,
};

@Component({
  selector: 'mockup-workflow-lanes-gallery',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ProjectWorkflowSectionComponent],
  providers: [{ provide: TaskService, useValue: STUB }],
  templateUrl: './workflow-lanes-gallery.component.html',
  styleUrl: './workflow-lanes-gallery.component.scss',
})
export class WorkflowLanesGalleryComponent {}
