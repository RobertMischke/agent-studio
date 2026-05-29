import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import type { TaskInfo } from '../../../../../models/task.model';
import type { RunCommitInfo, RunRecord } from '../../../../../features/run-timeline';
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
 * software-side change set (`/api/jobs/.../runs/{n}/commits`) and
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

  /**
   * Whether the per-run cards list is expanded. Default collapsed: a job
   * with 12 failed runs (the original symptom) takes well over half the
   * viewport otherwise. Collapsed view shows only the latest run's chip
   * plus aggregate counts; clicking the header toggles full list.
   */
  readonly listExpanded = signal<boolean>(false);

  toggleListExpanded() { this.listExpanded.update(v => !v); }

  readonly latestRun = computed<RunRecord | null>(() => {
    const v = this.visibleRuns();
    return v.length > 0 ? v[0] : null;
  });

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

  /** Most recent first - the user usually wants to see what just happened. */
  readonly visibleRuns = computed(() => [...this.runs()].sort((a, b) => b.index - a.index));

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
      return;
    }
    this.expandedIndex.set(index);
    this.loadCommits(index);
  }

  closePopover(): void {
    this.expandedIndex.set(null);
  }

  /**
   * Order for the icon row: chronological (oldest → newest), so the
   * timeline reads left-to-right like a progress bar. The full-card
   * list above continues to show newest-first because that's what
   * matters when the user wants to read what just happened.
   */
  readonly iconRuns = computed(() => [...this.runs()].sort((a, b) => a.index - b.index));

  /** Single character glyph for the icon. Uses the run index when small,
   *  otherwise a status-shaped marker so the row stays calm. */
  iconChar(r: RunRecord): string {
    if (r.index <= 99) return String(r.index);
    if (r.status === 'failed') return '✕';
    if (r.status === 'running') return '●';
    if (r.status === 'completed') return '✓';
    return '·';
  }

  /** Multi-line tooltip carrying the full info the verbose card showed. */
  iconTooltip(r: RunRecord): string {
    const parts: string[] = [
      `Run #${r.index} — ${this.statusLabel(r)} (${this.intentLabel(r.intent)})`,
      `Started ${this.formatTime(r.startedAt)}`
    ];
    if (r.durationSeconds != null) parts.push(`Duration: ${this.formatDuration(r.durationSeconds)}`);
    if (r.exitCode != null) parts.push(`Exit code: ${r.exitCode}`);
    if (r.userFollowup) parts.push(`Follow-up: ${r.userFollowup}`);
    if (r.reason) parts.push(`Reason: ${r.reason}`);
    parts.push('Click to expand');
    return parts.join('\n');
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
    return s === 0 ? `${m}m` : `${m}m ${s}s`;
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
}
