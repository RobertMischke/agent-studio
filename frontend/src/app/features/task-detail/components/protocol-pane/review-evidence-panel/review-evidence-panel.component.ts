import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';

import {
  TaskInfo,
  ReviewEvidenceEntry,
  ReviewEvidenceSeverity,
} from '../../../../../models/task.model';

import { TooltipDirective } from '@coding-agent/chat/shared';
import { formatDateTimeUtc } from '../../../../../services/format.util';
/**
 * Renders the per-task **review evidence** panel: findings from security
 * audits, code-review passes, task checks, or human notes that landed in
 * the job's `results/review-evidence.jsonl` file. The panel is purely
 * advisory — these findings are never blockers for state transitions.
 *
 * Each finding renders as a row with:
 *   - severity chip (info / warn / high),
 *   - source label, timestamp, run index when available,
 *   - title + body,
 *   - linked artifacts / file references,
 *   - "Acknowledge" toggle,
 *   - "Create follow-up task" action that posts to the API and emits
 *     the new job id so the parent can navigate.
 *
 * The component is presentational: data comes in via @Input, state changes
 * leave via @Output. The parent owns API calls and the routing decision
 * after a follow-up is created.
 */
@Component({
  selector: 'app-review-evidence-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective],
  templateUrl: './review-evidence-panel.component.html',
  styleUrl: './review-evidence-panel.component.scss',
})
export class ReviewEvidencePanelComponent {
  readonly entries = input.required<ReviewEvidenceEntry[]>();
  readonly job = input.required<TaskInfo>();

  readonly acknowledge = output<{ entry: ReviewEvidenceEntry; acknowledged: boolean }>();
  readonly createFollowup = output<ReviewEvidenceEntry>();

  /** Id of the row whose action is currently in flight (disables both buttons). */
  readonly busyId = signal<string | null>(null);

  /**
   * Stable order: high severity first, then warn, then info; ties broken by
   * createdAt ascending so the user reads findings chronologically inside a
   * severity bucket.
   */
  sorted = computed<ReviewEvidenceEntry[]>(() => {
    const rank: Record<ReviewEvidenceSeverity, number> = { high: 0, warn: 1, info: 2 };
    return [...this.entries()].sort((a, b) => {
      const ra = rank[a.severity] ?? 3;
      const rb = rank[b.severity] ?? 3;
      if (ra !== rb) return ra - rb;
      return new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime();
    });
  });

  severityLabel(s: ReviewEvidenceSeverity): string {
    if (s === 'high') return 'HIGH';
    if (s === 'warn') return 'WARN';
    return 'INFO';
  }

  sourceLabel(s: string): string {
    switch (s) {
      case 'security-audit':
        return 'Security audit';
      case 'code-review':
        return 'Code review';
      case 'task-check':
        return 'Task check';
      case 'human-note':
        return 'Human note';
      default:
        return 'Other';
    }
  }

  formatTime(iso: string): string {
    return formatDateTimeUtc(iso);
  }

  onToggleAck(e: ReviewEvidenceEntry): void {
    if (this.busyId()) return;
    this.busyId.set(e.id);
    this.acknowledge.emit({ entry: e, acknowledged: !e.acknowledged });
  }

  onCreateFollowup(e: ReviewEvidenceEntry): void {
    if (this.busyId() || e.followupJobId) return;
    this.busyId.set(e.id);
    this.createFollowup.emit(e);
  }

  /** Parent calls this once its API request resolves so the row re-enables. */
  clearBusy(): void {
    this.busyId.set(null);
  }
}
