import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import type { TaskInfo } from '../../../../../models/task.model';
import type { RunCommitInfo, RunRecord } from '../../../../../features/run-timeline';
import type { TaskTokenSummary } from '../../../../../features/tokens';
import { TaskService } from '../../../../../services/task.service';

import { TooltipDirective } from '../../../../../components/tooltip';
/**
 * Run timeline panel rendered above the activity log in the protocol
 * pane. Each card represents one CLI invocation between user inputs
 * (one "run" - the unit of conversation defined in
 * `docs/design-principles.md`). The collapsed card shows:
 *
 * - intent badge (start / continue / recovery / restart)
 * - status badge (running / completed / failed / cancelled)
 * - the user follow-up that triggered the run, if any
 * - duration + exit code
 *
 * Clicking a card expands it. The expanded card requests the
 * software-side change set (`/api/tasks/.../runs/{n}/commits`) and
 * renders the commit list. The activity-log filter for the run's
 * line-span is the next iteration; this component already emits the
 * selected run via `runSelected` so the parent can apply it.
 *
 * The component owns no run data of its own - it reads the timeline
 * from RunTimelinePollService and the commits from TaskService on
 * demand. That keeps the polling cadence in one place and avoids
 * stale per-card state when the timeline updates.
 */
@Component({
  selector: 'app-run-timeline',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './run-timeline.component.html',
  styleUrl: './run-timeline.component.scss'
})
export class RunTimelineComponent {
  readonly job = input<TaskInfo | null>(null);
  readonly runs = input<RunRecord[]>([]);

  /** Emits when the user clicks "Filter activity log to this run". */
  readonly runFilter = output<RunRecord>();

  /** Emits when the user clicks "Open git viewer" on a run card. */
  readonly openGitViewer = output<RunRecord>();

  readonly expandedIndex = signal<number | null>(null);

  readonly statusSummary = computed(() => {
    const v = this.visibleRuns();
    let completed = 0, failed = 0, running = 0, other = 0;
    for (const r of v) {
      switch (r.status) {
        case 'completed': completed++; break;
        case 'failed':    failed++; break;
        case 'running':   running++; break;
        default:          other++; break;
      }
    }
    return { completed, failed, running, other };
  });

  // Per-card commit state. Keyed by run.index so a re-poll of the
  // timeline doesn't wipe a card's loaded commits.
  readonly commitsByRun = signal<Map<number, RunCommitInfo[]>>(new Map());
  readonly commitsState = signal<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  readonly commitsError = signal<string | null>(null);

  // Per-card passed-context state. The context (rendered prompt) can be
  // multi-KB, so it is fetched lazily only when the user reveals it, keyed
  // by run.index so the cached text survives a timeline re-poll.
  readonly contextExpandedIndex = signal<number | null>(null);
  readonly contextByRun = signal<Map<number, string | null>>(new Map());
  readonly contextState = signal<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  readonly contextError = signal<string | null>(null);

  readonly contextText = computed<string | null>(() => {
    const idx = this.contextExpandedIndex();
    if (idx == null) return null;
    return this.contextByRun().get(idx) ?? null;
  });

  /** Chronological order - reissues must read as separate run segments. */
  readonly visibleRuns = computed(() => [...this.runs()].sort((a, b) => a.index - b.index));

  readonly commits = computed<RunCommitInfo[]>(() => {
    const idx = this.expandedIndex();
    if (idx == null) return [];
    return this.commitsByRun().get(idx) ?? [];
  });

  readonly totalAdded = computed(() => this.commits().reduce((s, c) => s + c.added, 0));
  readonly totalRemoved = computed(() => this.commits().reduce((s, c) => s + c.removed, 0));
  readonly totalFiles = computed(() => this.commits().reduce((s, c) => s + c.filesChanged, 0));

  private readonly jobService = inject(TaskService);

  toggle(index: number): void {
    if (this.expandedIndex() === index) {
      this.expandedIndex.set(null);
      this.contextExpandedIndex.set(null);
      return;
    }
    this.expandedIndex.set(index);
    this.contextExpandedIndex.set(null);
    this.loadCommits(index);
  }

  closePopover(): void {
    this.expandedIndex.set(null);
    this.contextExpandedIndex.set(null);
  }

  /** Reveal / hide the context block for a run, fetching the text on first reveal. */
  toggleContext(index: number): void {
    if (this.contextExpandedIndex() === index) {
      this.contextExpandedIndex.set(null);
      return;
    }
    this.contextExpandedIndex.set(index);
    this.loadContext(index);
  }

  nextRunAfter(index: number): RunRecord | null {
    const runs = this.visibleRuns();
    const pos = runs.findIndex(r => r.index === index);
    return pos >= 0 && pos + 1 < runs.length ? runs[pos + 1] : null;
  }

  transitionLabel(current: RunRecord, next: RunRecord): string {
    const trigger = next.userFollowup ? 'user follow-up' : this.intentLabel(next.intent);
    return `Run #${current.index} re-opened into #${next.index} via ${trigger}`;
  }

  cliIcon(cli: string | null): string {
    switch ((cli ?? '').toLowerCase()) {
      case 'codex': return 'Cx';
      case 'claude': return 'C';
      case 'copilot': return 'GH';
      case 'gemini': return 'G';
      default: return 'CLI';
    }
  }

  cliLabel(cli: string | null): string {
    return cli?.trim() || 'CLI';
  }

  emitFilter(r: RunRecord): void {
    this.runFilter.emit(r);
  }

  intentLabel(intent: string): string {
    switch (intent) {
      case 'start': return 'start';
      case 'continue': return 'continue';
      case 'recovery': return 'recovery';
      case 'restart': return 'restart';
      default: return intent || 'run';
    }
  }

  statusLabel(r: RunRecord): string {
    if (r.status === 'running') return 'running';
    if (r.status === 'completed') return 'completed';
    if (r.status === 'failed') return 'failed';
    // 'stopped' is the deliberate-kill status (user pause, Pause-&-Send,
    // watchdog). Legacy run records may still carry 'cancelled'.
    if (r.status === 'stopped' || r.status === 'cancelled') return 'stopped';
    return r.status || 'unknown';
  }

  formatDuration(seconds: number): string {
    if (seconds < 1) return '<1s';
    if (seconds < 60) return `${seconds.toFixed(0)}s`;
    const m = Math.floor(seconds / 60);
    const s = Math.round(seconds % 60);
    return s === 0 ? `${m}m` : `${m}m${s}s`;
  }

  tokenLabel(summary: TaskTokenSummary | null | undefined): string | null {
    if (!summary || summary.totalTokens <= 0) return null;
    return `${this.compactNumber(summary.totalTokens)} tok`;
  }

  private compactNumber(value: number): string {
    if (value >= 1_000_000) return `${(value / 1_000_000).toFixed(value >= 10_000_000 ? 0 : 1)}M`;
    if (value >= 1_000) return `${(value / 1_000).toFixed(value >= 10_000 ? 0 : 1)}k`;
    return value.toFixed(0);
  }

  formatTime(iso: string): string {
    const d = new Date(iso);
    if (isNaN(d.getTime())) return iso;
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  short(id: string): string {
    if (!id) return '';
    return id.length > 12 ? id.slice(0, 8) + '…' : id;
  }

  private loadCommits(index: number): void {
    const job = this.job();
    if (!job) return;
    if (this.commitsByRun().has(index)) {
      // Already loaded once; don't re-fetch on re-expand. The user can
      // close + reopen the card if they need a refresh, and the parent
      // poll cycle will refresh the timeline data itself.
      this.commitsState.set('loaded');
      this.commitsError.set(null);
      return;
    }
    this.commitsState.set('loading');
    this.commitsError.set(null);
    this.jobService.getRunCommits(job.id, index, job.watchPath).subscribe({
      next: (res) => {
        const map = new Map(this.commitsByRun());
        map.set(index, res.commits);
        this.commitsByRun.set(map);
        this.commitsState.set('loaded');
      },
      error: (err) => {
        this.commitsError.set(err?.error?.error || err?.message || 'Could not load commits.');
        this.commitsState.set('error');
      }
    });
  }

  private loadContext(index: number): void {
    const job = this.job();
    if (!job) return;
    if (this.contextByRun().has(index)) {
      // Cached from a previous reveal; the context for a finished run never
      // changes, so don't re-fetch the multi-KB payload.
      this.contextState.set('loaded');
      this.contextError.set(null);
      return;
    }
    this.contextState.set('loading');
    this.contextError.set(null);
    this.jobService.getRunContext(job.id, index, job.watchPath).subscribe({
      next: (res) => {
        const map = new Map(this.contextByRun());
        map.set(index, res.context ?? null);
        this.contextByRun.set(map);
        this.contextState.set('loaded');
      },
      error: (err) => {
        this.contextError.set(err?.error?.error || err?.message || 'Could not load context.');
        this.contextState.set('error');
      }
    });
  }
}
