import { ChangeDetectionStrategy, Component, OnDestroy, computed, inject, input, output, signal, ViewChild } from '@angular/core';
import { CliOutputLine, ContinueMode, JobDetail, JobSummaryStatus, ReviewEvidenceEntry, RunRecord } from '../../../models/job.model';
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
import { RunTimelinePollService } from '../run-timeline-poll.service';
import { ScreenshotsPollService } from '../screenshots-poll.service';
import { ScreenshotStripComponent } from '../../../features/screenshots/components/screenshot-strip/screenshot-strip.component';
import { NowTickService } from '../../../services/now-tick.service';
import { RunTimelineComponent } from './run-timeline.component';
import { RunGitViewerComponent } from './run-git-viewer.component';
import { CommonModule } from '@angular/common';
import { FeatureFlagsService } from '../../../services/feature-flags.service';
import { VerboseDebugOverlayComponent } from '../../../features/verbose-debug/components/verbose-debug-overlay.component';
import { HygieneStripComponent } from '../hygiene-strip/hygiene-strip.component';
import { ReviewEvidencePanelComponent } from './review-evidence-panel.component';
import { JobService } from '../../../services/job.service';
import type { RawLineRange } from '../../chat/conversation-event';

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
  imports: [CommonModule, ActivityLogViewComponent, RunTimelineComponent, RunGitViewerComponent, ScreenshotStripComponent, VerboseDebugOverlayComponent, HygieneStripComponent, ReviewEvidencePanelComponent],
  templateUrl: './protocol-pane.component.html',
  styleUrls: ['./protocol-pane.component.scss']
})
export class ProtocolPaneComponent implements OnDestroy {
  readonly detail = input.required<JobDetail>();
  readonly maximized = input(false);
  readonly weight = input<number>(1);
  readonly isRunning = input(false);
  /** Whether this job is the runner's currently-active job for its project. Forwarded to the hygiene strip so the worktree-isolation rule kicks in. */
  readonly isActiveJob = input<boolean>(false);

  readonly activeInspectorTab = input<InspectorTab>('protocol');
  readonly followupPrompt = input<string>('');
  readonly continueMode = input<ContinueMode>('continue');
  readonly canSendChat = input(false);
  readonly chatSendLabel = input<string>('Send');

  readonly regenerating = input(false);

  readonly maximizeToggle = output<void>();
  readonly hide = output<void>();
  /** Emitted after a follow-up task was created from a review-evidence finding so the parent can refetch the detail and (optionally) navigate to the new job. */
  readonly followupCreatedFromEvidence = output<{ jobId: string; targetState: string }>();
  /** Emitted after a finding was acknowledged so the parent can refetch the detail. */
  readonly evidenceMutated = output<void>();

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
  private readonly runTimelinePoll = inject(RunTimelinePollService);
  private readonly screenshotsPoll = inject(ScreenshotsPollService);
  private readonly nowTick = inject(NowTickService).now;
  private readonly jobs = inject(JobService);

  /** Set after "Create follow-up" returns; used to render the success banner. */
  readonly followupCreated = signal<{ jobId: string; targetState: string } | null>(null);

  readonly claudeSession = this.claudePoll.session;
  readonly claudeRateLimit = this.claudePoll.rateLimit;
  readonly cliOutput = this.cliPoll.output;
  readonly runTimeline = this.runTimelinePoll.timeline;
  readonly screenshots = this.screenshotsPoll.screenshots;

  /** Feature-flagged VS Code-style chrome. The "i" header button is only
   *  rendered when the flag is on; otherwise legacy chrome stays visible. */
  readonly featureFlags = inject(FeatureFlagsService);

  toggleVsCodeMeta(): void {
    this.featureFlags.setVsCodeMetaOpen(!this.featureFlags.vsCodeMetaOpen());
  }

  /**
   * Active run filter for the activity log. Set when the user clicks
   * "Filter activity log to this run" on a run-card; cleared via the
   * banner. The filter is line-span-based so we can re-apply it
   * deterministically as cliOutput grows during a live run.
   */
  readonly runFilterRange = signal<{ index: number; lineStart: number; lineEnd: number } | null>(null);

  /**
   * The activity-log lines visible in the embedded view. When a run
   * filter is active, slice cliOutput to the run's [lineStart..lineEnd]
   * (inclusive, 1-based). Otherwise return the full buffer. Open-ended
   * runs (still streaming) keep the upper bound at the buffer length so
   * new lines stream into the filter.
   */
  readonly filteredCliOutput = computed<CliOutputLine[]>(() => {
    const all = this.cliOutput();
    const range = this.runFilterRange();
    if (!range) return all;
    const start = Math.max(1, range.lineStart) - 1;
    const end = Math.min(all.length, range.lineEnd);
    if (end <= start) return [];
    return all.slice(start, end);
  });

  onRunFilter(r: RunRecord): void {
    if (r.lineStart == null) return;
    const end = r.lineEnd ?? this.cliOutput().length;
    this.runFilterRange.set({ index: r.index, lineStart: r.lineStart, lineEnd: end });
  }

  clearRunFilter(): void {
    this.runFilterRange.set(null);
  }

  // Big Git Viewer modal state. Opened from a run card via the
  // "Open git viewer" button; closed via the modal's own close
  // affordance or backdrop click. The selected RunRecord is held as
  // a signal so the modal re-binds its load when the user opens a
  // different run without closing first.
  readonly gitViewerRun = signal<RunRecord | null>(null);

  openGitViewer(r: RunRecord): void {
    if (!r.headShaBefore || !r.headShaAfter) return;
    this.gitViewerRun.set(r);
  }

  closeGitViewer(): void {
    this.gitViewerRun.set(null);
  }

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
   * Progressive spinner label so a slow Haiku call doesn't look frozen.
   * The backend caps the call at HaikuTimeoutSeconds = 90 s; we
   * intentionally mirror that constant here. Tiers:
   *   < 30 s         "Generating protocol..."
   *   30 s ... 60 s  "Generating protocol... (>=30 s)"
   *   >= 60 s        "Generating protocol... (>=60 s, will time out)"
   * Re-evaluates on every NowTickService tick while summaryStatus is
   * 'generating'; falls back to the base label as soon as the state
   * flips to ready or failed.
   */
  readonly summarySpinnerLabel = computed<string>(() => {
    if (this.summaryStatus() !== 'generating') return 'Generating protocol...';
    const startedAtIso = this.detail().summaryState?.startedAt;
    if (!startedAtIso) return 'Generating protocol...';
    const elapsed = (this.nowTick() - new Date(startedAtIso).getTime()) / 1000;
    if (elapsed >= 60) return 'Generating protocol... (>=60 s, will time out)';
    if (elapsed >= 30) return 'Generating protocol... (>=30 s)';
    return 'Generating protocol...';
  });

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

  /**
   * Drives the read-only Verbose Debug overlay. Opened from the activity-log
   * header; closed via the overlay's own close button. Trace links route to
   * the existing log overlay so the raw activity log remains one click away.
   */
  readonly verboseDebugOpen = signal(false);

  onVerboseDebugOpenTrace(_range: RawLineRange): void {
    // Route trace links to the existing activity-log maximized view so the
    // raw activity log stays the single source of truth for line-level
    // inspection. Closing Verbose Debug first keeps focus deterministic.
    this.verboseDebugOpen.set(false);
    this.openLogOverlay.emit();
  }

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

  /**
   * The activity-log view raises this when the user clicks an option button
   * on a steer card. We mirror the quick-reply behavior: pre-fill the
   * compose box so the user can edit and confirm, never auto-send.
   */
  onSteerOptionApply(option: string): void {
    if (!option) return;
    this.followupPromptChange.emit(option);
  }

  /**
   * The activity-log view raises this when the user clicks "Send screenshot"
   * on a steer card whose Need line mentions a screenshot. We open the job's
   * attachment uploader (a hidden &lt;input type=file&gt; in the template).
   */
  onSteerUploadRequest(): void {
    const input = document.querySelector<HTMLInputElement>(
      '[data-testid="orchestrator-steer-upload-input"]'
    );
    input?.click();
  }

  /**
   * Posts the chosen file to the job's attachments endpoint. Mirrors the
   * upload path used by the prompt editor (`/api/jobs/{id}/attachments`)
   * so the screenshot lands next to other task attachments where the
   * orchestrator can reference it on the next decision call.
   */
  async onSteerFileSelected(file: File | undefined | null): Promise<void> {
    if (!file) return;
    const job = this.detail()?.info;
    if (!job?.id) return;
    const watchPath = job.watchPath ?? '';
    const url = `/api/jobs/${encodeURIComponent(job.id)}/attachments`
      + (watchPath ? `?watchPath=${encodeURIComponent(watchPath)}` : '');
    const form = new FormData();
    form.append('file', file, file.name || 'steer-screenshot.png');
    try {
      await fetch(url, { method: 'POST', body: form });
    } catch {
      /* upload failure is best-effort; the user retains the steer card
         so they can try again or send a follow-up message instead */
    }
  }

  onEvidenceAcknowledge(
    payload: { entry: ReviewEvidenceEntry; acknowledged: boolean },
    panel: { clearBusy(): void }
  ): void {
    const job = this.detail().info;
    this.jobs
      .acknowledgeReviewEvidence(job.id, payload.entry.id, payload.acknowledged, job.watchPath)
      .subscribe({
        next: () => {
          panel.clearBusy();
          this.evidenceMutated.emit();
        },
        error: () => panel.clearBusy()
      });
  }

  onEvidenceCreateFollowup(
    entry: ReviewEvidenceEntry,
    panel: { clearBusy(): void }
  ): void {
    const job = this.detail().info;
    this.jobs
      .createReviewEvidenceFollowup(job.id, entry.id, {}, job.watchPath)
      .subscribe({
        next: (resp) => {
          panel.clearBusy();
          this.followupCreated.set({ jobId: resp.jobId, targetState: resp.targetState });
          this.followupCreatedFromEvidence.emit(resp);
          this.evidenceMutated.emit();
        },
        error: () => panel.clearBusy()
      });
  }

  dismissFollowupBanner(): void {
    this.followupCreated.set(null);
  }

  onOpenFollowup(jobId: string): void {
    const watch = this.detail().info.watchPath;
    const url = `/?job=${encodeURIComponent(jobId)}&watchPath=${encodeURIComponent(watch)}`;
    // Use full navigation: the protocol pane is mounted inside a job-detail
    // view that owns its own routing state, and a follow-up task is in a
    // different `?job=` slot. A full navigation re-mounts cleanly.
    if (typeof window !== 'undefined') {
      window.location.href = url;
    }
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
