import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { JobDetail } from '../../../models/job.model';
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
export type StatusViewMode = 'preview' | 'markdown';

/**
 * Protocol pane: shows status.md (preview / markdown / edit), the
 * activity log, the chat-compose strip, and Claude telemetry chips in
 * the header. State that the parent owns (edit toggles, drafts) is
 * passed in/out via inputs+outputs; live signals (cliOutput, claude
 * session/rate-limit) come from the locally-provided services.
 */
@Component({
  selector: 'app-protocol-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, ActivityLogViewComponent],
  templateUrl: './protocol-pane.component.html',
  styleUrls: ['./protocol-pane.component.scss']
})
export class ProtocolPaneComponent {
  readonly detail = input.required<JobDetail>();
  readonly maximized = input(false);
  readonly weight = input<number>(1);
  readonly isRunning = input(false);

  // Status-edit state owned by the parent (preserved across pane mounts)
  readonly editingStatus = input(false);
  readonly statusViewMode = input<StatusViewMode>('preview');
  readonly statusDraft = input<string>('');

  readonly activeInspectorTab = input<InspectorTab>('protocol');
  readonly followupPrompt = input<string>('');
  readonly canSendChat = input(false);
  readonly chatSendLabel = input<string>('Send');

  readonly maximizeToggle = output<void>();
  readonly hide = output<void>();

  readonly activeInspectorTabChange = output<InspectorTab>();
  readonly statusViewModeChange = output<StatusViewMode>();
  readonly statusDraftChange = output<string>();
  readonly followupPromptChange = output<string>();

  readonly startEditStatus = output<void>();
  readonly cancelEditStatus = output<void>();
  readonly saveStatus = output<void>();
  readonly statusKeydown = output<KeyboardEvent>();

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
