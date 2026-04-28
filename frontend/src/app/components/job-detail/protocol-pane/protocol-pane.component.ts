import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { JobDetail, JobSummaryStatus } from '../../../models/job.model';
import { ActivityLogViewComponent } from '../../activity-log-view';
import { markdownToHtml } from '../../markdown-utils';
import {
  formatTokens as fmtTokens,
  formatRateWindow as fmtRateWindow,
  formatResetIn as fmtResetIn
} from '../../../services/format.util';
import { ClaudeSessionPollService } from '../claude-session-poll.service';
import { CliOutputPollService } from '../cli-output-poll.service';
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
export class ProtocolPaneComponent {
  readonly detail = input.required<JobDetail>();
  readonly maximized = input(false);
  readonly weight = input<number>(1);
  readonly isRunning = input(false);

  readonly activeInspectorTab = input<InspectorTab>('protocol');
  readonly followupPrompt = input<string>('');
  readonly canSendChat = input(false);
  readonly chatSendLabel = input<string>('Send');

  readonly maximizeToggle = output<void>();
  readonly hide = output<void>();

  readonly activeInspectorTabChange = output<InspectorTab>();
  readonly followupPromptChange = output<string>();

  readonly openLogOverlay = output<void>();
  readonly sendChat = output<void>();
  readonly stopJob = output<void>();

  // Live data — injected from the parent's local providers.
  private readonly claudePoll = inject(ClaudeSessionPollService);
  private readonly cliPoll = inject(CliOutputPollService);
  private readonly nowTick = inject(NowTickService).now;

  readonly claudeSession = this.claudePoll.session;
  readonly claudeRateLimit = this.claudePoll.rateLimit;
  readonly cliOutput = this.cliPoll.output;

  readonly summaryStatus = computed<JobSummaryStatus>(
    () => this.detail().summaryState?.status ?? 'none'
  );

  // "There is or was activity for this job" — drives the live-dot indicator.
  // True when CLI is running OR we have any output buffered OR the job has a
  // log/usage record from a previous run.
  readonly hasActivity = computed(() => {
    if (this.isRunning()) return true;
    if (this.cliOutput().length > 0) return true;
    const d = this.detail();
    return d.log.length > 0 || d.info.lastUsage != null;
  });

  formatTokens(n: number): string { return fmtTokens(n); }
  formatRateWindow(window: string | null): string { return fmtRateWindow(window); }
  formatResetIn(epoch: number): string { return fmtResetIn(epoch, this.nowTick()); }

  renderMarkdown(md: string): string { return markdownToHtml(md); }

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
