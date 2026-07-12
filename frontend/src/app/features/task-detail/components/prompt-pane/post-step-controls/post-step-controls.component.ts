import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { OnDemandPostStepAttempt } from '../../../../task-pipeline';
import { TaskPipelinePollService } from '../../../../polling/services/task-pipeline-poll.service';
import { StudioTabStateService } from '../../../../studio-shell/services/studio-tab-state.service';
import { NotificationService } from '../../../../../services/notification.service';
import { TaskService } from '../../../../../services/task.service';

const SUPPORTED_STEP_IDS = new Set([
  'post-wiki-maintenance',
  'post-wiki-learnings',
  'post-agents-wiki-sync',
]);

@Component({
  selector: 'app-post-step-controls',
  standalone: true,
  imports: [TooltipDirective],
  templateUrl: './post-step-controls.component.html',
  styleUrl: './post-step-controls.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PostStepControlsComponent {
  readonly stepId = input.required<string>();
  readonly label = input.required<string>();
  readonly jobId = input.required<string>();
  readonly watchPath = input<string | undefined>();
  readonly projectName = input.required<string>();
  readonly enabledSource = input<'project' | 'catalogue'>('catalogue');
  readonly plannedStepIds = input<readonly string[]>([]);
  readonly attempts = input<readonly OnDemandPostStepAttempt[]>([]);

  private readonly jobs = inject(TaskService);
  private readonly notifs = inject(NotificationService);
  private readonly pipelinePoll = inject(TaskPipelinePollService);
  private readonly studioTabs = inject(StudioTabStateService);

  readonly busy = signal(false);
  readonly supported = computed(() => SUPPORTED_STEP_IDS.has(this.stepId()));
  readonly attemptCount = computed(() => this.attempts()
    .filter(attempt => attempt.stepId === this.stepId())
    .reduce((max, attempt) => Math.max(max, attempt.attempt), 0));
  readonly source = computed<'card' | 'project' | 'catalogue'>(() =>
    this.plannedStepIds().includes(this.stepId()) ? 'card' : this.enabledSource());

  run(): void {
    if (!this.supported() || this.busy()) return;
    this.busy.set(true);
    this.jobs.runTaskPostStep(this.jobId(), this.stepId(), this.watchPath()).subscribe({
      next: result => {
        this.busy.set(false);
        this.pipelinePoll.refresh();
        this.notifs.success(
          `${this.label()} attempt #${result.attempt}: ${result.summary}`,
          'Post-step finished',
        );
      },
      error: () => {
        this.busy.set(false);
        this.notifs.warning(`${this.label()} could not be run.`, 'Post-step failed');
      },
    });
  }

  openPipelineSettings(): void {
    this.studioTabs.open({ kind: 'hub', projectName: this.projectName(), section: 'pipeline' });
  }
}
