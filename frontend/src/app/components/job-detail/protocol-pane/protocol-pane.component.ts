import { ChangeDetectionStrategy, Component, OnDestroy, computed, inject, input, output, signal } from '@angular/core';
import { JobDetail, JobSummaryStatus } from '../../../models/job.model';
import { ActivityLogViewComponent } from '../../activity-log-view';
import { markdownToHtml, MarkdownImageOptions } from '../../markdown-utils';
import { resolveProtocolImageSrc } from './protocol-image-resolver';
import { copyTextToClipboard } from '../../../services/clipboard.util';
import {
  formatTokens as fmtTokens,
  formatRateWindow as fmtRateWindow,
  formatResetIn as fmtResetIn
} from '../../../services/format.util';
import { ClaudeSessionPollService } from '../claude-session-poll.service';
import { CliOutputPollService } from '../cli-output-poll.service';
import { SessionEventsPollService } from '../session-events-poll.service';
import { NowTickService } from '../../../services/now-tick.service';

export type InspectorTab = 'protocol' | 'activity';

/**
 * Protocol pane: shows the Haiku-generated status.md (read-only), the
 * activity log, the chat-compose strip, and Claude telemetry chips in
 * the header. status.md is owned by the SummaryGenerationService on the
 * backend — there is no edit mode here.
 */
@Component({
  selector: 'app-protocol-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ActivityLogViewComponent],
  templateUrl: './protocol-pane.component.html',
  styleUrls: ['./protocol-pane.component.scss']
})
export class ProtocolPaneComponent implements OnDestroy {
  readonly detail = input.required<JobDetail>();
  readonly maximized = input(false);
  readonly weight = input<number>(1);
  readonly isRunning = input(false);

  readonly activeInspectorTab = input<InspectorTab>('protocol');
  readonly followupPrompt = input<string>('');
  readonly canSendChat = input(false);
  readonly chatSendLabel = input<string>('Send');

  readonly regenerating = input(false);

  readonly maximizeToggle = output<void>();
  readonly hide = output<void>();

  readonly activeInspectorTabChange = output<InspectorTab>();
  readonly followupPromptChange = output<string>();

  readonly openLogOverlay = output<void>();
  readonly sendChat = output<void>();
  readonly stopJob = output<void>();
  readonly regenerateSummary = output<void>();

  // Live data — injected from the parent's local providers.
  private readonly claudePoll = inject(ClaudeSessionPollService);
  private readonly cliPoll = inject(CliOutputPollService);
  private readonly sessionEventsPoll = inject(SessionEventsPollService);
  private readonly nowTick = inject(NowTickService).now;

  readonly claudeSession = this.claudePoll.session;
  readonly claudeRateLimit = this.claudePoll.rateLimit;
  readonly cliOutput = this.cliPoll.output;

  /**
   * Drives the session-status chip in the header. Shape:
   *   - kind: "continued" | "lost" | "fresh" | null
   *   - chainLength: number of real session ids ever recorded
   *   - segmentCount: bumps every time the chain breaks (recovery)
   *   - tooltip: human-readable summary of the latest event
   * Returns null when there are no events yet (never been started).
   */
  readonly sessionChip = computed(() => {
    const r = this.sessionEventsPoll.response();
    if (!r || r.events.length === 0) return null;
    const last = r.events[r.events.length - 1];
    const chainLength = r.sessionChain.filter((s) => s && s !== '(recovery)').length;
    const segmentCount = r.sessionChain.filter((s) => s === '(recovery)').length + (chainLength > 0 ? 1 : 0);

    let kind: 'continued' | 'lost' | 'fresh';
    let label: string;
    let emoji: string;
    if (last.kind === 'recovery') {
      kind = 'lost';
      emoji = '⚠';
      label = 'session lost — recovered';
    } else if (last.kind === 'continue') {
      kind = 'continued';
      emoji = '✓';
      label = `session continued${chainLength > 1 ? ` (chain: ${chainLength})` : ''}`;
    } else {
      kind = 'fresh';
      emoji = '●';
      label = 'session started';
    }

    const reasonLine = last.reason ? `Reason: ${last.reason}\n` : '';
    const inputLine = last.inputSessionId ? `Resumed from: ${last.inputSessionId}\n` : '';
    const capturedLine = last.capturedSessionId ? `Captured: ${last.capturedSessionId}\n` : '';
    const tooltip = [
      `Last event: ${last.kind} (${last.cli ?? '?'})`,
      `When: ${last.ts}`,
      reasonLine.trim(),
      inputLine.trim(),
      capturedLine.trim(),
      `Chain length: ${chainLength}`,
      segmentCount > 1 ? `Chain breaks: ${segmentCount - 1}` : ''
    ].filter(Boolean).join('\n');

    return { kind, label, emoji, tooltip, chainLength, segmentCount };
  });

  readonly summaryStatus = computed<JobSummaryStatus>(
    () => this.detail().summaryState?.status ?? 'none'
  );

  // While the job is in 3-progress, the live Aktivität feed is what the user
  // came here to see — surface it as the leftmost tab. Outside that state we
  // keep the historical Protokoll-first order so the summary stays primary.
  readonly inProgress = computed(() => this.detail().info.state === '3-progress');

  // The button is meaningful only after the task has produced a cli-output.log.
  // We can't see the disk from here, so use "summary has been touched" as a
  // proxy: any non-`none` status means the runner already attempted to summarize
  // (which only happens after a successful CLI run wrote logs/cli-output.log).
  readonly canRegenerate = computed(() => {
    const status = this.summaryStatus();
    if (status === 'generating') return false;
    if (this.regenerating()) return false;
    return status !== 'none' || !!this.detail().statusMarkdown;
  });

  // "There is or was activity for this job" — drives the live-dot indicator.
  // True when CLI is running OR we have any output buffered OR the job has a
  // log/usage record from a previous run.
  readonly hasActivity = computed(() => {
    if (this.isRunning()) return true;
    if (this.cliOutput().length > 0) return true;
    const d = this.detail();
    return d.log.length > 0 || d.info.lastUsage != null;
  });

  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');
  private copyResetTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnDestroy(): void {
    if (this.copyResetTimer !== null) {
      clearTimeout(this.copyResetTimer);
      this.copyResetTimer = null;
    }
  }

  copyLabel(): string {
    const s = this.copyState();
    if (s === 'copied') return '✓ Copied';
    if (s === 'failed') return '⚠ Copy failed';
    return '📋 Copy';
  }

  async copyProtocolMarkdown(): Promise<void> {
    const md = this.detail().statusMarkdown ?? '';
    if (!md) return;
    const ok = await copyTextToClipboard(md);
    this.copyState.set(ok ? 'copied' : 'failed');
    if (this.copyResetTimer !== null) clearTimeout(this.copyResetTimer);
    this.copyResetTimer = setTimeout(() => {
      this.copyState.set('idle');
      this.copyResetTimer = null;
    }, 2000);
  }

  formatTokens(n: number): string { return fmtTokens(n); }
  formatRateWindow(window: string | null): string { return fmtRateWindow(window); }
  formatResetIn(epoch: number): string { return fmtResetIn(epoch, this.nowTick()); }

  renderMarkdown(md: string): string {
    return markdownToHtml(md, this.markdownOptions());
  }

  private markdownOptions(): MarkdownImageOptions {
    const info = this.detail().info;
    return {
      resolveImageSrc: (src) => resolveProtocolImageSrc(src, info.id, info.watchPath)
    };
  }

  claudeSessionTooltip(): string {
    const cs = this.claudeSession();
    if (!cs) return '';
    return [
      `Model: ${cs.model ?? '?'}`,
      `Input: ${cs.inputTokens.toLocaleString()} tokens`,
      `Output: ${cs.outputTokens.toLocaleString()} tokens`,
      `Cache read: ${cs.cacheReadTokens.toLocaleString()} tokens`,
      `Cache creation: ${cs.cacheCreationTokens.toLocaleString()} tokens`,
      `Turns recorded: ${cs.turnCount}`,
      cs.lastTurnAt ? `Last turn: ${cs.lastTurnAt}` : ''
    ].filter(Boolean).join('\n');
  }

  rateLimitTooltip(): string {
    const rl = this.claudeRateLimit();
    if (!rl) return '';
    const reset = rl.resetsAt
      ? new Date(rl.resetsAt * 1000).toLocaleString()
      : 'unknown';
    return [
      `Window: ${this.formatRateWindow(rl.window)}`,
      `Status: ${rl.status ?? '?'}`,
      `Resets at: ${reset}`,
      `Overage: ${rl.overageStatus ?? '—'}`,
      rl.isUsingOverage ? 'Currently using overage budget' : '',
      `Captured: ${new Date(rl.capturedAt).toLocaleTimeString()}`
    ].filter(Boolean).join('\n');
  }
}
