import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { TaskState, type TaskInfo, type TaskTestEvidenceSource, type TaskTestRunEvidence } from '../../../../models/task.model';

type TestEvidenceContext = Pick<TaskInfo, 'id' | 'watchPath' | 'state' | 'commit' | 'commits' | 'integration' | 'testEvidence'>;
export type TestEvidenceStatusVariant = 'card' | 'panel';

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

function hasAttributedDelivery(task: TestEvidenceContext): boolean {
  return (task.commits?.length ?? 0) > 0
    || !!task.commit
    || !!task.integration?.deliveryRef?.trim();
}

export function visibleTestEvidence(task: TestEvidenceContext): TaskTestRunEvidence | null {
  const evidence = task.testEvidence;
  if (!evidence) return null;
  if (hasRecordedEvidence(evidence)) return evidence;
  return hasAttributedDelivery(task) || MISSING_EVIDENCE_RELEVANT_STATES.has(task.state)
    ? evidence
    : null;
}

@Component({
  selector: 'app-test-evidence-status',
  standalone: true,
  imports: [AppTooltipDirective],
  templateUrl: './test-evidence-status.component.html',
  styleUrl: './test-evidence-status.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TestEvidenceStatusComponent {
  readonly task = input.required<TestEvidenceContext>();
  readonly variant = input<TestEvidenceStatusVariant>('card');
  readonly testId = input('task-card-test-evidence');
  readonly evidence = computed(() => visibleTestEvidence(this.task()));

  tooltipText(): string {
    const evidence = this.evidence();
    if (!evidence) return '';
    const details = evidence.runId
      ? [`Project test run ${evidence.runId} at ${evidence.runCommit || 'unknown commit'}`]
      : [];
    details.push(...(evidence.sources ?? []).map(source => source.reason));
    return details.length > 0 ? details.join(' · ') : evidence.summary;
  }

  sourceKey(source: TaskTestEvidenceSource): string {
    return `${source.kind}:${source.id}:${source.reportRef}`;
  }

  compactSourceDetail(): string {
    const sources = this.evidence()?.sources ?? [];
    if (sources.length > 1) return sources.slice(1).map(source => source.summary).join(' · ');
    if (sources.length === 1) return `${this.sourceLabel(sources[0].kind)} · ${sources[0].id}`;
    return '';
  }

  resultLabel(result: string): string {
    if (result === 'passed') return 'Pass';
    if (result === 'failed') return 'Failed';
    if (result === 'blocked') return 'Blocked';
    if (result === 'not-applicable') return 'Not applicable';
    return 'Not proven';
  }

  reportHref(source: TaskTestEvidenceSource): string | null {
    const jobId = this.task().id?.trim();
    const reportRef = source.reportRef?.trim();
    if (!jobId || !reportRef || reportRef.includes('..')) return null;
    const segments = reportRef.replace(/\\/g, '/').split('/').filter(Boolean);
    if (segments.length === 0) return null;
    const path = segments.map(encodeURIComponent).join('/');
    const watchPath = this.task().watchPath?.trim();
    const query = new URLSearchParams({ scope: 'workspace' });
    if (watchPath) query.set('watchPath', watchPath);
    return `/api/tasks/${encodeURIComponent(jobId)}/files/${path}?${query.toString()}`;
  }

  private sourceLabel(kind: string): string {
    if (kind === 'review-build-tests') return 'Remote review';
    if (kind === 'review-aspects') return 'Review aspects';
    if (kind === 'pre-develop-build-gate') return 'Pre-develop gate';
    if (kind === 'pre-main-test-gate') return 'Pre-main gate';
    return 'Build/test gate';
  }
}
