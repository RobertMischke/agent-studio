import { ChangeDetectionStrategy, Component, OnDestroy, computed, inject, input, output, signal } from '@angular/core';
import { ContinueMode, JobDetail, JobSummaryStatus } from '../../../models/job.model';
import { deriveWatchdogPill } from './watchdog-state';
import { ActivityLogViewComponent } from '../../activity-log-view';
import { markdownToHtml, MarkdownImageOptions } from '../../markdown-utils';
import { buildConversationTurns, parseActivityLog } from '../../activity-log.parser';
import { classifyOutcome, OutcomeAssessment, QuickReply } from '../../agent-outcome.util';
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
  readonly continueMode = input<ContinueMode>('continue');
  readonly canSendChat = input(false);
  readonly chatSendLabel = input<string>('Send');

  readonly regenerating = input(false);

  readonly maximizeToggle = output<void>();
  readonly hide = output<void>();

  readonly activeInspectorTabChange = output<InspectorTab>();
  readonly followupPromptChange = output<string>();
  readonly continueModeChange = output<ContinueMode>();

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

  /**
   * Watchdog pill state derived purely from polled output frames + the
   * NowTickService clock. Re-evaluates whenever cliOutput or the clock
   * tick changes; the chip in the header reads .visible to decide
   * whether to render at all and uses .label / .state / .tooltip for
   * the visual.
   */
  readonly watchdogPill = computed(() => deriveWatchdogPill({
    lines: this.cliOutput(),
    isRunning: this.isRunning(),
    now: new Date(this.nowTick())
  }));

  /**
   * Order + labels for the mode pills above the chat input. Each option has a
   * short icon glyph, a one-word title for the pill, and a tooltip the user
   * sees on hover so the meaning is discoverable without leaving the page.
   */
  readonly modeOptions: ReadonlyArray<{ id: ContinueMode; title: string; icon: string; tooltip: string }> = [
    { id: 'continue', title: 'Continue', icon: '➤',
      tooltip: 'Send as the next conversation turn (default).' },
    { id: 'steer', title: 'Steer', icon: '↺',
      tooltip: 'Course correction: agent overrides its current plan and adopts your direction.' },
    { id: 'extend', title: 'Extend', icon: '＋',
      tooltip: 'Add to the task. Backend writes a new prompt-N.md so the task description grows blog-style.' },
    { id: 'newTask', title: 'New task', icon: '✦',
      tooltip: 'Start a new sub-task in the same session. Prior context preserved, new request.' }
  ];

  /** Compose-area placeholder, mode-aware. */
  composePlaceholder(): string {
    switch (this.continueMode()) {
      case 'steer':   return 'Course correction — what should the agent do differently? Ctrl+Enter to send.';
      case 'extend':  return 'Extend the task — this becomes a new prompt-N.md alongside the original. Ctrl+Enter to send.';
      case 'newTask': return 'New sub-task in this session — describe the new request. Ctrl+Enter to send.';
      default:        return 'Type a follow-up — Ctrl+Enter to send. Sends while running pauses the agent first.';
    }
  }

  // While the job is in 3-progress, the live Activity feed is what the user
  // came here to see — surface it as the leftmost tab. Outside that state we
  // keep the historical Protocol-first order so the summary stays primary.
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

  /**
   * Heuristic classification of the agent's last reply (see
   * {@link classifyOutcome}). Drives the auto-eval banner above the chat
   * input: a one-line summary of where the agent landed plus up to four
   * quick-reply chips that pre-fill the follow-up prompt. While the CLI is
   * actively running we suppress the banner — the "outcome" is still in
   * flight and chips would race the streaming text.
   */
  readonly outcome = computed<OutcomeAssessment | null>(() => {
    if (this.isRunning()) return null;
    const lines = this.cliOutput();
    if (lines.length === 0) return null;
    const groups = parseActivityLog(lines);
    const turns = buildConversationTurns(groups);
    // Walk from the end. We are looking for the agent's most recent reply,
    // but we must not jump past a *newer* failed-run signal: a system-error
    // turn (e.g. claude's "No conversation found with session ID ..." +
    // error_during_execution) means the latest run never produced a real
    // agent reply, even though earlier runs did. Returning the stale agent
    // text from a previous run would make the chip banner claim "Agent is
    // mid-task" for a run that actually errored. The orchestrator already
    // posted a [capture-fail] decision in that case; keep the banner
    // honest by surfacing the failed-run state instead of the old reply.
    let lastAgent: string | null = null;
    let sawErrorAfterAgent = false;
    for (let i = turns.length - 1; i >= 0; i--) {
      const t = turns[i];
      if (t.kind === 'agent') {
        lastAgent = t.text;
        break;
      }
      if (t.kind === 'system' && t.status === 'error') {
        sawErrorAfterAgent = true;
      }
    }
    if (sawErrorAfterAgent) {
      // Surface the failed-run state explicitly so the user gets a
      // verbindliches Signal that something went wrong on this turn,
      // rather than a silent or misleading "mid-task" banner. The chips
      // pre-fill the chat input; the backend's capture-fail handling
      // already cleared the dead session id, so a normal "Continue"
      // follow-up routes through Recovery on the next run.
      return {
        kind: 'failed',
        summary: 'Last run ended with an error — agent did not produce a reply.',
        question: null,
        suggestions: [
          { label: 'Continue (rebuild)', prompt: 'Continue from where the previous run left off — rebuild context from the job folder.' },
          { label: 'Retry as new task', prompt: 'Treat this as a fresh request and start over: ' }
        ]
      };
    }
    return classifyOutcome(lastAgent ?? '');
  });

  /** True when the auto-eval banner should be visible. */
  readonly outcomeVisible = computed(() => {
    const o = this.outcome();
    if (!o) return false;
    if (o.kind === 'unknown' && !o.question) return false;
    return o.suggestions.length > 0;
  });

  /** Maps outcome kind to a short emoji glyph for the banner badge. */
  outcomeEmoji(kind: string): string {
    switch (kind) {
      case 'done': return '✓';
      case 'blocked': return '⚠';
      case 'failed': return '✗';
      case 'question': return '?';
      case 'needs_input': return '?';
      case 'progress': return '⏳';
      default: return 'i';
    }
  }

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

  /**
   * Quick-reply chip click handler. Always pre-fills the input rather than
   * sending immediately — the user reviews and confirms before the follow-up
   * goes out. The default-false `autoSend` flag on individual chips is the
   * future hook for one-click sends, kept here for symmetry but not wired up
   * until we have telemetry that says it would not surprise users.
   */
  applyQuickReply(reply: QuickReply): void {
    this.followupPromptChange.emit(reply.prompt);
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
