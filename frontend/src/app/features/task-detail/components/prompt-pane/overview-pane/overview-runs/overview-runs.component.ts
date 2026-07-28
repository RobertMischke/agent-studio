import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { RunRecord } from '../../../../../run-timeline';
import type { CliType } from '../../../../../../models/task.model';
import {
  cliTypeLabel,
  formatCompactDateTime,
  formatDateTime,
  formatTokens,
  shortModelName,
} from '../../../../../../services/format.util';
import { formatDuration } from '../overview-pane-formatters';

type RunResultTone = 'success' | 'danger' | 'warning' | 'active' | 'neutral';

const KNOWN_CLIS: readonly CliType[] = ['claude', 'codex', 'gemini'];

interface OverviewRunVm {
  record: RunRecord;
  startedAt: string | null;
  startedAtTitle: string | null;
  trigger: string;
  result: string;
  resultTone: RunResultTone;
  duration: string;
  engine: string | null;
  tokens: string | null;
  reason: string | null;
}

@Component({
  selector: 'app-overview-runs',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './overview-runs.component.html',
  styleUrl: './overview-runs.component.scss',
})
export class OverviewRunsComponent {
  /** Run records from the currently open task only. */
  readonly runs = input<readonly RunRecord[]>([]);

  /**
   * Persisted CORE duration used only when an interrupted run has no timeline
   * duration. The visible run rows remain exclusively timeline-backed.
   */
  readonly fallbackDurationSeconds = input(0);

  /** Latest run first, preserving each timeline index as the durable row id. */
  readonly rows = computed<OverviewRunVm[]>(() =>
    [...this.runs()]
      .sort((left, right) => right.index - left.index)
      .map((run) => ({
        record: run,
        startedAt: this.startedAtLabel(run),
        startedAtTitle: this.startedAtTitle(run),
        trigger: this.triggerLabel(run),
        result: this.resultLabel(run),
        resultTone: this.resultTone(run),
        duration: this.durationLabel(run),
        engine: this.engineLabel(run),
        tokens: this.tokenLabel(run),
        reason: this.reasonLabel(run),
      })),
  );

  /** Aggregate count is always the sum of the visible card-scoped rows. */
  readonly runCount = computed(() => this.rows().length);

  readonly totalDurationSeconds = computed(() => {
    const recorded = this.rows().reduce(
      (total, row) => total + Math.max(0, row.record.durationSeconds ?? 0),
      0,
    );
    return recorded > 0 ? recorded : Math.max(0, this.fallbackDurationSeconds());
  });

  readonly hasContent = computed(() => this.runCount() > 0 || this.totalDurationSeconds() > 0);

  readonly countLabel = computed(() => {
    const count = this.runCount();
    return count === 1 ? '1 run' : `${count} runs`;
  });

  readonly totalDurationLabel = computed(
    () => `${formatDuration(this.totalDurationSeconds())} total`,
  );

  private triggerLabel(run: RunRecord): string {
    switch (run.intent.trim().toLowerCase()) {
      case 'start':
        return 'Initial start';
      case 'continue':
        return run.userFollowup?.trim() ? 'User follow-up' : 'Continue';
      case 'recovery':
        return 'Recovery';
      case 'restart':
        return 'Restart';
      case 'reissue':
        return 'Review reissue';
      default:
        return run.intent.trim() || 'Run';
    }
  }

  private resultLabel(run: RunRecord): string {
    switch (run.status.trim().toLowerCase()) {
      case 'completed':
        return 'Completed';
      case 'failed':
        return 'Failed';
      case 'running':
        return 'Running';
      case 'stopped':
      case 'cancelled':
        return 'Stopped';
      case 'interrupted':
        return 'Interrupted';
      default:
        return run.status.trim() || 'Unknown';
    }
  }

  private resultTone(run: RunRecord): RunResultTone {
    switch (run.status.trim().toLowerCase()) {
      case 'completed':
        return 'success';
      case 'failed':
        return 'danger';
      case 'stopped':
      case 'cancelled':
      case 'interrupted':
        return 'warning';
      case 'running':
        return 'active';
      default:
        return 'neutral';
    }
  }

  private durationLabel(run: RunRecord): string {
    if (run.durationSeconds != null && run.durationSeconds >= 0) {
      return formatDuration(run.durationSeconds);
    }
    return run.status.trim().toLowerCase() === 'running' ? 'In progress' : 'Not recorded';
  }

  private tokenLabel(run: RunRecord): string | null {
    const usage = run.tokenSummary;
    if (!usage) return null;
    return `${formatTokens(Math.max(0, usage.totalTokens))} tokens`;
  }

  private startedAtLabel(run: RunRecord): string | null {
    return this.hasValidStart(run) ? formatCompactDateTime(run.startedAt) : null;
  }

  private startedAtTitle(run: RunRecord): string | null {
    return this.hasValidStart(run) ? formatDateTime(run.startedAt) : null;
  }

  private hasValidStart(run: RunRecord): boolean {
    const raw = run.startedAt?.trim();
    return !!raw && !Number.isNaN(new Date(raw).getTime());
  }

  /**
   * The runner-resolved fields are authoritative for new runs. The execution
   * context and token rollup remain read fallbacks for older records.
   */
  private engineLabel(run: RunRecord): string | null {
    const cli = run.cli?.trim() ?? '';
    const cliLabel = (KNOWN_CLIS as readonly string[]).includes(cli)
      ? cliTypeLabel(cli as CliType)
      : cli;
    const rawModel =
      run.model?.trim()
      || run.executionContext?.model?.trim()
      || run.tokenSummary?.lastModel?.trim()
      || '';
    const model = rawModel ? shortModelName(rawModel) : '';
    const thinkingLevel = run.thinkingLevel?.trim() ?? '';
    const modelLabel = model && thinkingLevel ? `${model} · ${thinkingLevel}` : model;
    if (cliLabel && modelLabel) return `${cliLabel} · ${modelLabel}`;
    return cliLabel || modelLabel || null;
  }

  private reasonLabel(run: RunRecord): string | null {
    const reason = run.reason?.trim();
    if (!reason) return null;
    return this.resultTone(run) === 'success' ? null : reason;
  }
}
