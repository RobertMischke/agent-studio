import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { TaskState, type TaskInfo, type TaskTestRunEvidence } from '../../../../models/task.model';

type TaskTestEvidenceContext = Pick<TaskInfo, 'state' | 'commit' | 'commits' | 'integration' | 'testEvidence'>;

const MISSING_EVIDENCE_RELEVANT_STATES = new Set<string>([
  TaskState.AutoReview,
  TaskState.HumanReview,
  TaskState.Completed,
  TaskState.Archive,
]);

function hasRecordedEvidence(evidence: TaskTestRunEvidence): boolean {
  return evidence.evidenceState !== 'unassigned'
    || !!evidence.runId
    || (evidence.sources?.length ?? 0) > 0;
}

function hasAttributedDelivery(task: TaskTestEvidenceContext): boolean {
  return (task.commits?.length ?? 0) > 0
    || !!task.commit
    || !!task.integration?.deliveryRef?.trim();
}

export function visibleTaskTestEvidence(task: TaskTestEvidenceContext): TaskTestRunEvidence | null {
  const evidence = task.testEvidence;
  if (!evidence) return null;
  if (hasRecordedEvidence(evidence)) return evidence;
  return hasAttributedDelivery(task) || MISSING_EVIDENCE_RELEVANT_STATES.has(task.state)
    ? evidence
    : null;
}

@Component({
  selector: 'app-task-test-evidence',
  standalone: true,
  imports: [AppTooltipDirective],
  templateUrl: './task-test-evidence.html',
  styleUrl: './task-test-evidence.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskTestEvidenceComponent {
  readonly task = input.required<TaskTestEvidenceContext>();
  readonly evidence = computed(() => visibleTaskTestEvidence(this.task()));

  tooltipText(): string {
    const evidence = this.evidence();
    if (!evidence) return '';
    const details = evidence.runId
      ? [`Project test run ${evidence.runId} at ${evidence.runCommit || 'unknown commit'}`]
      : [];
    details.push(...(evidence.sources ?? []).map(source => source.summary));
    return details.length > 0 ? details.join(' · ') : evidence.summary;
  }

  sourceDetail(): string {
    const sources = this.evidence()?.sources ?? [];
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
