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
    const result = run.result?.trim().toLowerCase() ?? '';
    switch (result) {
      case 'done':
      case 'success':
        return 'Done';
      case 'completed':
        return 'Completed';
      case 'noop':
        return 'No-op';
      case 'failed':
      case 'environmentfailure':
        return 'Failed';
      case 'unverified':
        return 'Unverified';
      case 'superseded':
        return 'Superseded';
      case 'blocked':
        return 'Blocked';
      case 'needsinput':
      case 'needs-input':
        return 'Needs input';
      case 'stopped':
      case 'cancelled':
      case 'canceled':
        return 'Stopped';
      case 'interrupted':
        return 'Interrupted';
      case 'committed-partial':
        return 'Partial';
      case 'unknown':
        return 'Unknown';
    }
    if (this.isLegacyUnrecorded(run)) return 'Not recorded (legacy run)';

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
    switch ((run.result?.trim() || run.status.trim()).toLowerCase()) {
      case 'done':
      case 'success':
      case 'noop':
      case 'completed':
        return 'success';
      case 'environmentfailure':
      case 'unverified':
      case 'failed':
        return 'danger';
      case 'superseded':
      case 'blocked':
      case 'needsinput':
      case 'needs-input':
      case 'committed-partial':
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
    if (run.status.trim().toLowerCase() === 'running') return 'In progress';
    return this.isLegacyUnrecorded(run) ? 'Not recorded (legacy run)' : 'Not recorded';
  }

  private isLegacyUnrecorded(run: RunRecord): boolean {
    return !run.result?.trim() && run.closeoutSource?.startsWith('legacy-') === true;
  }

  private tokenLabel(run: RunRecord): string | null {
    const usage = run.tokenSummary;
    if (!usage) return null;
    return `${formatTokens(Math.max(0, usage.totalTokens))} tokens`;
  }

  /**
   * Absolute wall-clock start, never a relative "x ago": the rows are rendered
   * inside a polled change-detection pass, so a Date.now()-derived label would
   * churn (and risk NG0100). Unparseable stamps are dropped rather than shown
   * as "Invalid Date".
   */
  private startedAtLabel(run: RunRecord): string | null {
    return this.hasValidStart(run) ? formatCompactDateTime(run.startedAt) : null;
  }

  private startedAtTitle(run: RunRecord): string | null {
    return this.hasValidStart(run) ? formatDateTime(run.startedAt) : null;
  }

  private hasValidStart(run: RunRecord): boolean {
    const raw = run.startedAt?.trim();
    if (!raw) return false;
    return !Number.isNaN(new Date(raw).getTime());
  }

  /**
   * "Which agent ran this attempt" — CLI plus the model it actually reported.
   * The runner-resolved fields are authoritative for new runs; the run's own
   * execution context and token rollup remain read fallbacks for older
   * records, so a run never inherits the card's current model.
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

  /**
   * Why the run ended the way it did (recovery cause, escalation reason). Only
   * carried for runs that did not simply complete — a reason on a green run is
   * noise, and the full text stays available as the row's title.
   */
  private reasonLabel(run: RunRecord): string | null {
    const reason = run.reason?.trim();
    if (!reason) return null;
    return this.resultTone(run) === 'success' ? null : reason;
  }
}
