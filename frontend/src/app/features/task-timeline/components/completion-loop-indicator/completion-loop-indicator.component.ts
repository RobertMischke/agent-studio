import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { TaskState } from '../../../../models/task.model';
import { AspectFindingsListComponent } from '../../../../components/aspect-findings';
import { TaskTimelinePollService } from '../../../polling/services/task-timeline-poll.service';
import {
  verdictGlyph,
  verdictLabel,
  verdictTone,
  type CompletionLoopVerdict,
} from '../../models/task-timeline.model';

/**
 * Compact attempt-cycle indicator for the task-detail Overview tab
 * (ADR-0049 / ASS-566). Surfaces "where is the orchestrator's completion
 * loop right now": the latest verdict (accepted / reopened / escalated),
 * the current attempt against its budget, how many times the task has been
 * re-opened, and the one-line gap that drove the most recent reopen /
 * escalation. The full reopen->retry->verdict story lives in the Timeline
 * tab ({@link TaskTimelinePaneComponent}).
 *
 * Reads {@link TaskTimelinePollService} via DI — the same instance the
 * Overview pane and Timeline tab share (provided once on task-detail), so
 * all three render the same polled snapshot.
 */
@Component({
  selector: 'app-completion-loop-indicator',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective, AspectFindingsListComponent],
  templateUrl: './completion-loop-indicator.component.html',
  styleUrl: './completion-loop-indicator.component.scss',
})
export class CompletionLoopIndicatorComponent {
  private readonly poll = inject(TaskTimelinePollService);

  readonly taskState = input<string | null>(null);

  readonly completionLoop = this.poll.completionLoop;

  /** Only render once the loop has produced at least one verdict. */
  readonly hasCompletionLoop = computed(() => this.completionLoop().hasActivity);

  /** A later terminal lane move turns a contradictory verdict into history. */
  readonly isHistoricalOutcome = computed<boolean>(() => {
    const verdict = this.completionLoop().latestVerdict;
    switch (this.taskState()) {
      case TaskState.Completed: return verdict !== 'accepted';
      case TaskState.Escalated: return verdict !== 'escalated';
      case TaskState.Archive: return true;
      default: return false;
    }
  });

  readonly currentOutcomeLabel = computed<string | null>(() => {
    switch (this.taskState()) {
      case TaskState.Completed: return 'Delivered';
      case TaskState.Escalated: return 'Escalated';
      case TaskState.Archive: return 'Archived';
      default: return null;
    }
  });

  readonly displayedVerdictTone = computed(() =>
    this.isHistoricalOutcome() ? 'neutral' : this.verdictTone(this.completionLoop().latestVerdict),
  );

  readonly historicalOutcomeExplanation = computed<string | null>(() => {
    const outcome = this.currentOutcomeLabel();
    if (!this.isHistoricalOutcome() || !outcome) return null;
    if (this.taskState() === TaskState.Completed && this.completionLoop().latestVerdict === 'escalated') {
      return 'This earlier escalation belongs to a prior attempt. The task was reopened, continued, and later moved to Delivered.';
    }
    return `This earlier pipeline verdict was superseded when the task moved to ${outcome}.`;
  });

  /** "N / M" attempt counter, or just "N" when the budget is unknown. */
  readonly attemptLabel = computed<string | null>(() => {
    const loop = this.completionLoop();
    if (loop.attempt == null) return null;
    return loop.maxAttempts != null ? `${loop.attempt} / ${loop.maxAttempts}` : `${loop.attempt}`;
  });

  verdictLabel(v: CompletionLoopVerdict | null): string {
    return verdictLabel(v);
  }

  verdictGlyph(v: CompletionLoopVerdict | null): string {
    return verdictGlyph(v);
  }

  verdictTone(v: CompletionLoopVerdict | null): string {
    return verdictTone(v);
  }

  formatRelativeTime(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    const diffMs = Date.now() - d.getTime();
    const minutes = Math.round(diffMs / 60_000);
    if (minutes < 1) return 'just now';
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.round(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.round(hours / 24);
    if (days < 30) return `${days}d ago`;
    const months = Math.round(days / 30);
    if (months < 12) return `${months}mo ago`;
    return `${Math.round(months / 12)}y ago`;
  }

  formatAbsoluteTime(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toLocaleString();
  }
}
