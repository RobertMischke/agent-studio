import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import type { TaskTestRunEvidence } from '../../../../models/task.model';

@Component({
  selector: 'app-task-test-evidence',
  standalone: true,
  imports: [AppTooltipDirective],
  templateUrl: './task-test-evidence.html',
  styleUrl: './task-test-evidence.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskTestEvidenceComponent {
  readonly evidence = input.required<TaskTestRunEvidence>();

  tooltipText(): string {
    const evidence = this.evidence();
    const details = evidence.runId
      ? [`Project test run ${evidence.runId} at ${evidence.runCommit || 'unknown commit'}`]
      : [];
    details.push(...(evidence.sources ?? []).map(source => source.summary));
    return details.length > 0 ? details.join(' · ') : evidence.summary;
  }

  sourceDetail(): string {
    const sources = this.evidence().sources ?? [];
    if (sources.length > 1) return sources.slice(1).map(source => source.summary).join(' · ');
    if (sources.length === 1) return `${this.sourceLabel(sources[0].kind)} · ${sources[0].id}`;
    return '';
  }

  private sourceLabel(kind: string): string {
    if (kind === 'review-build-tests') return 'Remote review';
    if (kind === 'pre-develop-build-gate') return 'Pre-develop gate';
    if (kind === 'pre-main-test-gate') return 'Pre-main gate';
    return 'Build/test gate';
  }
}
