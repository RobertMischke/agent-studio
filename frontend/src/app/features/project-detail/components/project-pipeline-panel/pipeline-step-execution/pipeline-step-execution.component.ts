import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { TaskService } from '../../../../../services/task.service';
import type { PipelineStepProbeResult } from '../../../../task-pipeline';
import type { PipelineAdminRow } from '../pipeline-config.util';

@Component({
  selector: 'app-pipeline-step-execution',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pipeline-step-execution.component.html',
  styleUrl: './pipeline-step-execution.component.scss',
})
export class PipelineStepExecutionComponent {
  readonly projectName = input.required<string>();
  readonly step = input.required<PipelineAdminRow>();
  private readonly taskService = inject(TaskService);

  readonly busy = signal(false);
  readonly result = signal<PipelineStepProbeResult | null>(null);
  readonly error = signal<string | null>(null);

  commandLabel(workingSubdir: string, command: string): string {
    return workingSubdir ? `cd ${workingSubdir} && ${command}` : command;
  }

  runProbe(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.result.set(null);
    const step = this.step();
    this.taskService.probePipelineStep(this.projectName(), step.id).subscribe({
      next: result => {
        this.result.set(result);
        this.busy.set(false);
      },
      error: error => {
        this.error.set(error?.error?.error ?? error?.message ?? 'Step probe failed.');
        this.busy.set(false);
      },
    });
  }
}
