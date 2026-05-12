import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import type { AutoLoopSnapshot, JobInfo, PendingIntent } from '../../../../models/job.model';
import { GitSummaryService } from '../../../../services/git-summary.service';
import { ClientService } from '../../../../services/client.service';
import { AutoReviewStatusStore } from '../../../../services/auto-review-status.store';
import { cliTypeIcon } from '../../../../services/format.util';
import { projectIdentity } from '../../../../services/project-identity.util';
import { TagRegistryStore } from '../../../../services/tag-registry.store';
import { shouldShowFailureToast } from '../../../job-detail/services/run-outcome.util';

// Shared 'now' signal that ticks every 30s so all relative timestamps update in lockstep
// without re-reading Date.now() during change detection (which causes NG0100).
const nowTick = signal(Date.now());
if (typeof window !== 'undefined') {
  setInterval(() => nowTick.set(Date.now()), 30_000);
}

@Component({
  selector: 'app-job-card',
  standalone: true,
  // OnPush + signal-based reactivity. With ~30+ cards in a single
  // 4-auto-review lane, default Zone CD on every microtask was cumulating
  // into 80-100 ms long tasks during scroll/poll bursts. The component's
  // template only reads signal inputs, computed signals, and the shared
  // `nowTick` signal, so OnPush updates remain correct without any
  // explicit `markForCheck` calls.
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './job-card.component.html',
  styleUrl: './job-card.component.scss',
})
export class JobCardComponent implements OnInit, OnDestroy {
  readonly job = input.required<JobInfo>();
  readonly compact = input<boolean>(false);
  readonly deleteRequested = output<JobInfo>();
  private readonly gitSummary = inject(GitSummaryService);
  private readonly clients = inject(ClientService);
  private readonly tagRegistry = inject(TagRegistryStore);
  private readonly autoReviewStatus = inject(AutoReviewStatusStore);
  private stopPolling: (() => void) | null = null;

  /**
   * Backlog-lane spec: render the structural classification as a small
   * chip (🐞 Bug / ✨ Feature / · Chore). The chip is always visible — even
   * for the default `chore` value — so the user can scan a lane for
   * features vs technical work without filtering. Legacy `user-story`
   * values render as Feature so cards predating the rename keep a stable
   * chip.
   */
  readonly taskTypeChip = computed<{ kind: string; label: string; icon: string; tooltip: string } | null>(() => {
    const t = (this.job().taskType || 'chore').toLowerCase();
    if (t === 'bug') return { kind: 'bug', label: 'Bug', icon: '🐞', tooltip: 'Task type: Bug' };
    if (t === 'feature' || t === 'user-story') return { kind: 'feature', label: 'Feature', icon: '✨', tooltip: 'Task type: Feature' };
    return { kind: 'chore', label: 'Chore', icon: '·', tooltip: 'Task type: Chore (default)' };
  });

  /**
   * Tag chips on the card. Looks up label + colour from the workspace
   * registry signal; tags whose id no longer exists in the registry render
   * as a faint "ghost" chip with the raw id so the user knows to clean up.
   */
  readonly tagChips = computed(() => {
    const ids = this.job().tags ?? [];
    if (ids.length === 0) return [];
    const byId = this.tagRegistry.byId();
    return ids.map(id => {
      // Auto-review concern tags use the `<namespace>:concerns` shape and
      // are not in the registry by design (they are ephemeral findings,
      // not curated taxonomy). The card renders them with a small ⚠ chip
      // so the user sees the source aspect at a glance instead of a
      // generic "unknown tag" ghost. See ADR-0025.
      const concernMatch = /^([a-z][a-z0-9-]*):concerns$/i.exec(id);
      if (concernMatch) {
        const ns = concernMatch[1];
        return {
          id,
          label: `${ns}:concerns`,
          color: '#fbbf24',
          ghost: false,
          concern: true,
          tooltip: `Auto-review aspect '${ns}' flagged concerns. Open the job and read aspect-*.md for details.`
        };
      }
      const entry = byId.get(id);
      if (entry) {
        return {
          id,
          label: entry.label,
          color: entry.color,
          ghost: false,
          concern: false,
          tooltip: entry.description ? `${entry.label}: ${entry.description}` : entry.label
        };
      }
      return {
        id,
        label: id,
        color: '#475569',
        ghost: true,
        concern: false,
        tooltip: `Unknown tag '${id}'; registry entry was removed`
      };
    });
  });

  onDeleteClick(event: MouseEvent) {
    event.stopPropagation();
    this.deleteRequested.emit(this.job());
  }

  /**
   * Owner-attribution chip on every card. Resolves the job's
   * `ownerClientId` against the registry from /api/clients and renders
   * emoji + display name + the owner's chosen colour. Falls back to a
   * neutral placeholder when the registry has not loaded yet.
   */
  readonly ownerChip = computed<{
    id: string;
    label: string;
    emoji: string;
    background: string;
    border: string;
    foreground: string;
    tooltip: string;
  } | null>(() => {
    const ownerId = this.job().ownerClientId;
    if (!ownerId) return null;
    const c = this.clients.resolve(ownerId);
    const baseColour = c.colour || '#64748b';
    return {
      id: c.id,
      label: c.displayName || c.id,
      emoji: c.emoji || '·',
      background: this.tintFromHex(baseColour, 0.12),
      border: this.tintFromHex(baseColour, 0.32),
      foreground: '#e2e8f0',
      tooltip: `Owner: ${c.displayName || c.id} (${c.id})`
    };
  });

  private tintFromHex(hex: string, alpha: number): string {
    const m = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(hex.trim());
    if (!m) return `rgba(100,116,139,${alpha})`;
    let body = m[1];
    if (body.length === 3) body = body.split('').map(ch => ch + ch).join('');
    const r = parseInt(body.slice(0, 2), 16);
    const g = parseInt(body.slice(2, 4), 16);
    const b = parseInt(body.slice(4, 6), 16);
    return `rgba(${r},${g},${b},${alpha})`;
  }

  // Git status only matters for tasks the user is actively working on or
  // about to review — pre-work lanes (preparation/ready) and post-review
  // lanes (completed/archive) carry no useful per-task git context, so we
  // skip the pill there to keep the board calm.
  // ADR-0025: pill stays in both review lanes (auto + human).
  private static readonly LANES_WITH_GIT = new Set([
    '3-progress', '4-auto-review', '5-human-review', '4-review',
  ]);

  readonly gitPill = computed(() => {
    if (!JobCardComponent.LANES_WITH_GIT.has(this.job().state)) return null;
    const projectName = this.job().projectName;
    const summary = this.gitSummary.value().find(s => s.projectName === projectName);
    return summary && summary.isRepo ? summary : null;
  });

  readonly gitTooltip = computed(() => {
    const g = this.gitPill();
    if (!g) return '';
    return `Branch: ${g.branch ?? '(detached)'}\n${g.filesChanged} changed file(s) in ${g.rootPath}\n+${g.totalAdded} / −${g.totalRemoved}`;
  });

  readonly commitTooltip = computed(() => {
    const c = this.job().commit;
    if (!c) return '';
    const subject = (c.message || '').split('\n')[0];
    return `${c.shortSha} — ${subject}\n${c.filesChanged} file(s) changed`;
  });

  ngOnInit(): void { this.stopPolling = this.gitSummary.ensurePolling(); }
  ngOnDestroy(): void { this.stopPolling?.(); }

  stateLabel(): string {
    const state = this.job().state;
    const name = state.includes('-') ? state.substring(state.indexOf('-') + 1) : state;
    return name.replace(/-/g, ' ');
  }

  /**
   * Lifecycle-phase chip. Surfaces the `phase` substate on cards that
   * carry one (Ready group: human-ready / intake-running / intake-blocked /
   * intake-passed). Hidden when the job has no explicit phase, so cards
   * that predate the field render exactly like before.
   */
  phaseBadge(): { label: string; tone: 'human-ready' | 'intake-running' | 'intake-blocked' | 'intake-passed'; tooltip: string } | null {
    const phase = this.job().phase ?? null;
    if (!phase) return null;
    switch (phase) {
      case 'human-ready':
        return { label: 'Human Ready', tone: 'human-ready',
                 tooltip: 'The user marked this task ready. Orchestrator intake will check it before the coding runner picks it up.' };
      case 'intake-running':
        return { label: 'Intake running', tone: 'intake-running',
                 tooltip: 'Orchestrator intake is checking this card (separate runner from the coding CLI).' };
      case 'intake-blocked':
        return { label: 'Intake blocked', tone: 'intake-blocked',
                 tooltip: 'Orchestrator intake flagged this card. Check the activity log for the reason and resolve before the coding runner can pick it up.' };
      case 'intake-passed':
        return { label: 'Intake passed', tone: 'intake-passed',
                 tooltip: 'Orchestrator intake approved this card. The coding runner is now allowed to pick it up.' };
      default:
        return null;
    }
  }

  executionBadge(): { label: string; tone: 'running' | 'failed' | 'cancelled' } | null {
    const execution = this.job().execution;
    if (!execution) return null;

    if (execution.status === 'running') {
      return { label: 'Running live', tone: 'running' };
    }

    if (shouldShowFailureToast(execution)) {
      return { label: execution.exitCode === null ? 'Failed' : `Failed (${execution.exitCode})`, tone: 'failed' };
    }

    if (execution.runOutcome === 'noop') {
      return { label: 'NoOp', tone: 'cancelled' };
    }

    if (execution.runOutcome === 'blocked') {
      return { label: 'Blocked', tone: 'cancelled' };
    }

    if (execution.runOutcome === 'needs-input') {
      return { label: 'Needs input', tone: 'cancelled' };
    }

    // 'stopped' is the new deliberate-kill status from the backend
    // (user pause, Pause-&-Send, watchdog kill). Render as a calm
    // "Stopped" pill, not a failure. Legacy 'cancelled' value stays
    // supported so older in-memory CliExecution records keep rendering.
    if (execution.status === 'stopped' || execution.status === 'cancelled') {
      return { label: 'Stopped', tone: 'cancelled' };
    }

    return null;
  }

  /**
   * Review-pill descriptor: shows the auto-review (Haiku summarizer)
   * status on a card that landed in 4-auto-review. Returns null when
   * there is nothing to show (no run, or the user already moved on).
   */
  readonly reviewBadge = computed<{ label: string; tone: 'generating' | 'ready' | 'failed'; tooltip: string } | null>(() => {
    const s = this.job().summaryState;
    if (!s) return null;
    switch (s.status) {
      case 'generating':
        return { label: 'auto-reviewing', tone: 'generating',
                 tooltip: 'Orchestrator is summarizing the run output (Haiku). The card will become quiet once status.md has been written.' };
      case 'ready':
        return { label: 'review ready', tone: 'ready',
                 tooltip: s.bytesWritten ? `Auto-review wrote ${s.bytesWritten} bytes to status.md.` : 'Auto-review finished.' };
      case 'failed':
        return { label: 'review failed', tone: 'failed',
                 tooltip: s.errorMessage ?? 'Auto-review failed.' };
      default:
        return null;
    }
  });

  readonly autoReviewProcessBadge = computed<{ label: string; tone: 'active' | 'queued' | 'stale' | 'done'; tooltip: string } | null>(() => {
    const job = this.job();
    if (job.state !== '4-auto-review') return null;

    const s = this.autoReviewStatus.status();
    const matchesCurrent = !!s?.currentJob
      && s.currentJob === job.id
      && (!s.currentProject || s.currentProject === job.projectName);

    if (matchesCurrent) {
      return {
        label: 'reviewing now',
        tone: 'active',
        tooltip: 'Auto-review is currently running its multi-aspect pass for this task.'
      };
    }

    if (job.orchestratorVerdict) {
      return {
        label: `review ${job.orchestratorVerdict}`,
        tone: 'done',
        tooltip: `Auto-review has already recorded an orchestrator verdict: ${job.orchestratorVerdict}.`
      };
    }

    if (!s?.lastTickAt) {
      return {
        label: 'review pending',
        tone: 'queued',
        tooltip: 'This task is in Auto Review. The global auto-review status has not loaded yet.'
      };
    }

    const ageMs = Date.now() - Date.parse(s.lastTickAt);
    if (ageMs > 90_000) {
      return {
        label: 'review stale',
        tone: 'stale',
        tooltip: `Auto-review has not completed a tick since ${new Date(s.lastTickAt).toLocaleString()}.`
      };
    }

    return {
      label: 'queued for review',
      tone: 'queued',
      tooltip: `Auto-review is alive. Last tick saw ${s.pending ?? 0} candidate(s); this task is waiting in 4-auto-review.`
    };
  });

  readonly outcomeIssueBadge = computed<{ label: string; tone: 'info' | 'warn' | 'high'; tooltip: string } | null>(() => {
    const issue = this.job().outcomeIssue;
    if (!issue) return null;
    const severity = (issue.severity ?? '').toLowerCase();
    const tone = severity === 'high' ? 'high' : severity === 'warn' ? 'warn' : 'info';
    const seen = issue.lastSeenAt ? `\nLast seen: ${this.formatShortTime(issue.lastSeenAt)}` : '';
    const summary = issue.summary ? `\n\n${issue.summary}` : '';
    return {
      label: issue.label || issue.kind,
      tone,
      tooltip: `Runner outcome issue: ${issue.kind}${seen}${summary}`
    };
  });

  /** Hot-state threshold: amber pill once the loop is at 80% of the iteration cap. */
  readonly loopHot = computed(() => {
    const al = this.job().autoLoop;
    if (!al || al.maxIterations <= 0) return false;
    return al.iteration / al.maxIterations >= 0.8;
  });

  loopTooltip(al: AutoLoopSnapshot): string {
    const tokenLine = `${al.tokensUsed.toLocaleString()} / ${al.maxTokens.toLocaleString()} orchestrator tokens`;
    const startedAt = (() => { try { return new Date(al.startedAt).toLocaleString(); } catch { return al.startedAt; } })();
    const lastQ = (al.lastQuestion ?? '').slice(0, 160);
    const lastErr = al.lastError ? `\nLast error: ${al.lastError}` : '';
    return `Auto-loop: orchestrator answering NEEDS_INPUT for this job.\n` +
           `Iteration ${al.iteration} of ${al.maxIterations}.\n` +
           `${tokenLine}.\nStarted ${startedAt}.${lastErr}\n\nLast question: ${lastQ}${(al.lastQuestion ?? '').length > 160 ? '...' : ''}`;
  }

  pendingTooltip(pi: PendingIntent): string {
    const when = (() => {
      try { return new Date(pi.savedAt).toLocaleString(); }
      catch { return pi.savedAt; }
    })();
    const preview = (pi.prompt ?? '').slice(0, 120);
    return `Pending follow-up (${pi.mode}) saved ${when}.\nWill run on next auto-pickup.\n\n${preview}${(pi.prompt ?? '').length > 120 ? '...' : ''}`;
  }

  /** Compact tokens label: 850 -> "850", 2400 -> "2.4k", 850000 -> "850k", 3_100_000 -> "3.1M". */
  formatTokens(n: number): string {
    if (!isFinite(n) || n <= 0) return '0';
    if (n < 1000) return Math.round(n).toString();
    if (n < 1_000_000) {
      const k = n / 1000;
      return (k >= 100 ? Math.round(k) : Number(k.toFixed(1))) + 'k';
    }
    const m = n / 1_000_000;
    return (m >= 100 ? Math.round(m) : Number(m.toFixed(1))) + 'M';
  }

  /**
   * Token-bubble descriptor: returns null when the task has no recorded
   * orchestrator activity (input + output + cacheRead + cacheWrite == 0).
   * Tier thresholds match the prompt: < 50k neutral, < 500k blue,
   * < 5M mauve, otherwise peach.
   */
  readonly tokenBubble = computed<{
    label: string;
    total: number;
    input: number;
    output: number;
    cacheRead: number;
    cacheWrite: number;
    model: string | null;
    lastUpdate: string | null;
    tier: 'neutral' | 'blue' | 'mauve' | 'peach';
    entries: { ts: string; tsLabel: string; model: string | null; total: number }[];
  } | null>(() => {
    const t = this.job().tokenSummary;
    if (!t) return null;
    const input = t.inputTokens ?? 0;
    const output = t.outputTokens ?? 0;
    const cacheRead = t.cacheReadTokens ?? 0;
    const cacheWrite = t.cacheCreationTokens ?? 0;
    const total = input + output + cacheRead + cacheWrite;
    if (total <= 0) return null;
    const tier = total >= 5_000_000 ? 'peach'
      : total >= 500_000 ? 'mauve'
      : total >= 50_000 ? 'blue'
      : 'neutral';
    const entries = (t.entries ?? []).map(e => ({
      ts: e.ts,
      tsLabel: this.formatShortTime(e.ts),
      model: e.model,
      total: (e.inputTokens ?? 0) + (e.outputTokens ?? 0) + (e.cacheReadTokens ?? 0) + (e.cacheCreationTokens ?? 0)
    }));
    return {
      label: this.formatTokens(total),
      total,
      input,
      output,
      cacheRead,
      cacheWrite,
      model: t.lastModel ?? null,
      lastUpdate: t.lastUpdate ? this.formatShortTime(t.lastUpdate) : null,
      tier,
      entries
    };
  });

  private formatShortTime(iso: string): string {
    try {
      return new Date(iso).toLocaleString();
    } catch {
      return iso;
    }
  }

  readonly agentIcon = computed(() => {
    const t = this.job().cliType;
    return t ? cliTypeIcon(t) : '🤖';
  });

  readonly identity = computed(() => projectIdentity(this.job().projectName));

  readonly isRunning = computed(() => this.job().execution?.status === 'running');

  readonly relativeActivity = computed(() => {
    const dateStr = this.job().lastActivity;
    if (!dateStr) return 'never';
    const diff = nowTick() - new Date(dateStr).getTime();
    const mins = Math.floor(diff / 60000);
    if (mins < 1) return 'just now';
    if (mins < 60) return mins + 'm ago';
    const hrs = Math.floor(mins / 60);
    if (hrs < 24) return hrs + 'h ago';
    return Math.floor(hrs / 24) + 'd ago';
  });
}
