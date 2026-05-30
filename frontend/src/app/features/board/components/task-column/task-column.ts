import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output } from '@angular/core';
import { TaskInfo, TaskOrderItem, ProjectRunnerStatus } from '../../../../models/task.model';
import { TaskCardComponent } from '../task-card/task-card.component';
import { projectIdentity } from '../../../../services/project-identity.util';
import { cliTypeIcon } from '../../../../services/format.util';
import { TooltipDirective } from '../../../../components/tooltip';
import { groupReviewJobs } from '../review-grouping.util';
import { AutoReviewStatusStore } from '../../../../services/auto-review-status.store';
import { InfoButtonComponent } from '../../../../components/info-button/info-button.component';
import { laneDocTopic } from '../../../../components/info-button/lane-doc-topic';

const ARCHIVE_VISIBLE_LIMIT = 20;

@Component({
  selector: 'app-task-column, app-job-column',
  standalone: true,
  imports: [TaskCardComponent, TooltipDirective, InfoButtonComponent],
  // Cycle 7b: OnPush. The board mounts ~10 columns and re-renders the
  // full @for of cards every CD pass under Default. TaskCard is already
  // OnPush; promoting the column propagates that benefit upward so a
  // poll tick that didn't change THIS lane's jobs() input doesn't
  // walk the lane's children either. Inputs are signal-based so OnPush
  // marks dirty correctly without needing markForCheck.
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-column.html',
  styleUrl: './task-column.scss'
})
export class TaskColumnComponent implements OnInit, OnDestroy {
  private readonly autoReviewStatus = inject(AutoReviewStatusStore);

  readonly title = input.required<string>();
  readonly icon = input<string>('');
  readonly state = input.required<string>();
  readonly jobs = input.required<TaskInfo[]>();
  readonly reorderDisabled = input<boolean>(false);
  readonly collapsed = input<boolean>(false);
  readonly compact = input<boolean>(false);
  readonly archiving = input<boolean>(false);
  /** F2: id of a just-created card to highlight + scroll into view. */
  readonly highlightJobId = input<string | null>(null);
  /**
   * Project for which the lane shows an auto-pickup toggle. `null` hides
   * the chip — used when the board is scoped to "All projects" (no
   * single project to attribute the toggle to). Only the In-Progress
   * lane (state = '3-progress') reads these inputs today.
   */
  readonly autoProject = input<string | null>(null);
  /** Current runner mode for the auto project, drives the chip's on/off look. */
  readonly autoMode = input<string>('manual');
  /**
   * Full runner-status snapshot for the lane's auto project. Drives the
   * In-Progress lane's three-pill status cluster (RUNNING / mode / Q:N).
   * Null when the lane is not project-scoped (e.g. board scoped to "All
   * projects" with mixed projects in the lane).
   */
  readonly runnerStatus = input<ProjectRunnerStatus | null>(null);
  /**
   * Live wall-clock tick used so the RUNNING pill's duration string
   * (`3m24s`) advances without re-polling the runner status. The board
   * passes a 1-Hz signal; the column does the read inside a computed so
   * change detection is OnPush-friendly.
   */
  readonly nowMs = input<number>(0);

  readonly jobClick = output<TaskInfo>();
  // `targetIndex` is the 0-based insertion slot in this column the user
  // dropped the card on. Stable across silent polls because the backend
  // rewrites every sibling's `order` field when the move applies the
  // slot (see JobTransitionService.MoveAsync). Without it the card
  // keeps its source-lane order value and snaps to a stale position.
  readonly jobDrop = output<{ jobId: string; watchPath: string; targetState: string; targetIndex: number }>();
  readonly jobReorder = output<{ state: string; jobs: TaskOrderItem[] }>();
  readonly jobDeleteRequest = output<TaskInfo>();
  /** F5: bubbled "Pick next" click from a 2-ready card. */
  readonly jobPickNextRequest = output<TaskInfo>();
  readonly addTask = output<string>();
  readonly archiveAll = output<void>();
  readonly collapseToggle = output<void>();
  /** Emits the project name when the user clicks the lane's auto chip. */
  readonly autoToggleRequest = output<string>();

  /**
   * Three-pill status cluster for the In-Progress lane:
   * - `running`: shown when the runner has an active execution. Includes
   *   the JobId, an elapsed-time string, and the model.
   * - `mode`: the pickup-mode pill (AUTO / MANUAL / PAUSED). Distinguishes
   *   `manual` from circuit-breaker-induced halts: when the runner is in
   *   `manual` because of a circuit-breaker reason it renders as PAUSED
   *   with a different colour so the operator sees this was a system-
   *   initiated halt, not a user toggle.
   * - `queueSize`: count of items waiting in `2-ready` (`Q:5` badge).
   *
   * Rendered as a horizontal cluster on the lane header. Only active for
   * `state() === '3-progress'` and only when a single project is in scope
   * (the existing `autoProject()` gate).
   */
  readonly statusCluster = computed(() => {
    if (this.state() !== '3-progress' || !this.autoProject()) return null;
    const status = this.runnerStatus();
    const mode = this.autoMode();
    const reason = status?.modeReason ?? null;
    const source = status?.modeSource ?? null;
    const role = status?.role ?? null;
    const pendingMode = status?.pendingMode ?? null;
    const pendingAfter = status?.pendingModeWillApplyAfter ?? null;

    // PAUSED kind: explicit `paused` mode OR `manual` mode that was
    // flipped by a circuit-breaker / supervisor. The visible chip is the
    // same in both cases, but the tooltip names the actual cause.
    const isCircuitBreaker =
      source === 'circuit-breaker' || (reason ?? '').includes('circuit-breaker');
    const isSupervisorPause = source === 'supervisor';
    let modeKind: 'auto' | 'manual' | 'paused';
    let modeLabel: string;
    let modeTooltip: string;
    if (mode === 'auto-continuous') {
      modeKind = 'auto';
      modeLabel = 'AUTO';
      modeTooltip = 'Auto-pickup: when the active task finishes, the runner will start the next item in 2-ready automatically.';
    } else if (mode === 'auto-single') {
      modeKind = 'auto';
      modeLabel = 'AUTO · 1';
      modeTooltip = 'Auto-pickup (single shot): the runner will start one more task and then revert to manual.';
    } else if (mode === 'paused' || isCircuitBreaker || isSupervisorPause) {
      modeKind = 'paused';
      modeLabel = 'PAUSED';
      if (isCircuitBreaker) {
        modeTooltip = `Auto-pickup paused by circuit-breaker (${reason ?? 'consecutive failures'}). Click the auto toggle to re-enable.`;
      } else if (isSupervisorPause) {
        modeTooltip = `Auto-pickup paused by supervisor (${reason ?? 'supervisor intervention'}). Click the auto toggle to re-enable.`;
      } else {
        modeTooltip = 'Runner paused. No new tasks will be picked up until you re-enable auto.';
      }
    } else {
      modeKind = 'manual';
      modeLabel = 'MANUAL';
      modeTooltip = 'Auto-pickup is off. The currently running task continues; new tasks have to be started manually.';
    }

    // ADR-0044: deferred-mode overlay. A PUT /api/runner/{project}/mode call
    // that arrived while a job was active leaves the live mode at its
    // auto-* value and queues the requested mode in status.pendingMode.
    // Surface that as "(after current)" on the pill so the operator sees the
    // change took, just not yet.
    if (pendingMode) {
      const pendingPretty = pendingMode === 'paused' ? 'PAUSED' : pendingMode.toUpperCase();
      modeLabel = `${modeLabel} → ${pendingPretty}`;
      modeTooltip =
        `${modeTooltip}\n\nDeferred change pending: mode will flip to "${pendingMode}" when the active job (${pendingAfter ?? 'in flight'}) finishes (ADR-0044).`;
    }

    // ADR-0044: test-subject backends never auto-pick. The label still
    // shows the configured mode (operators can leave it on auto for
    // future role flips), but we annotate the tooltip so the lane pill
    // explains why nothing is being claimed even when AUTO is on.
    if (role === 'test-subject') {
      modeTooltip =
        `${modeTooltip}\n\nThis backend is the test-subject seat (ADR-0044). The auto-pickup loop is structurally disabled regardless of mode; only explicit /api/tasks/{id}/start calls (Playwright fixtures, manual debugging) reach the CLI.`;
    }

    // Pick the active run. Two signals feed this:
    //   (a) `status.activeExecution`: what THIS backend's runner is driving.
    //       Authoritative when present.
    //   (b) any 3-progress card whose `execution.status === 'running'`: the
    //       disk-derived signal that survives across backends. In a shared-
    //       workspace setup another backend (e.g. dev next to stable, both
    //       watching the same project) may have picked up the task; the local
    //       runner then has `activeJobId=null` but the lane is genuinely live.
    //       Without (b) the RUNNING pill went missing and the operator was
    //       left looking at a bare MANUAL pill while a task was actually
    //       running — the bug behind this fix.
    // When only (b) matches the pill is flagged `foreign: true` so the UI can
    // hint that this backend isn't the one driving the run.
    let runningPill: {
      jobId: string;
      duration: string;
      model: string | null;
      foreign: boolean;
      tooltip: string;
    } | null = null;
    const ownExec = status?.activeExecution ?? null;
    const ownRunning = ownExec && ownExec.status === 'running' ? ownExec : null;
    if (ownRunning) {
      const startedAt = Date.parse(ownRunning.startedAt);
      const now = this.nowMs() || Date.now();
      const elapsedMs = isFinite(startedAt) ? Math.max(0, now - startedAt) : 0;
      const duration = this.formatElapsed(elapsedMs);
      runningPill = {
        jobId: ownRunning.jobId,
        duration,
        model: ownRunning.model ?? null,
        foreign: false,
        tooltip: `Currently running: ${ownRunning.jobId}${ownRunning.model ? ` (${ownRunning.model})` : ''}. Started ${duration} ago.`
      };
    } else {
      const foreignJob = this.jobs().find(j => j.execution?.status === 'running' && !!j.execution.startedAt);
      const foreignExec = foreignJob?.execution ?? null;
      if (foreignExec) {
        const startedAt = Date.parse(foreignExec.startedAt);
        const now = this.nowMs() || Date.now();
        const elapsedMs = isFinite(startedAt) ? Math.max(0, now - startedAt) : 0;
        const duration = this.formatElapsed(elapsedMs);
        runningPill = {
          jobId: foreignExec.jobId,
          duration,
          model: foreignExec.model ?? null,
          foreign: true,
          tooltip: `Currently running: ${foreignExec.jobId}${foreignExec.model ? ` (${foreignExec.model})` : ''}. Started ${duration} ago. This run is being driven by another backend on the shared workspace; this backend's runner is not in control.`
        };
      }
    }

    // When a foreign backend owns the active run, soften the MANUAL/AUTO
    // tooltip so the operator doesn't read MANUAL as "nothing is happening".
    // The mode label itself stays accurate (it describes what THIS backend's
    // runner will do once the current run finishes).
    if (runningPill?.foreign) {
      if (modeKind === 'manual') {
        modeTooltip = 'Auto-pickup on this backend is off. A run from another backend on the shared workspace is currently in progress (see RUNNING pill). Enable auto if you want this backend to take the next task.';
      } else if (modeKind === 'auto') {
        modeTooltip = 'Auto-pickup is on for this backend. A run from another backend on the shared workspace is currently in progress; once it ends, this backend will pick the next 2-ready task automatically.';
      }
    }

    const queueSize = status?.queuedJobIds?.length ?? 0;

    return {
      running: runningPill,
      mode: { kind: modeKind, label: modeLabel, tooltip: modeTooltip },
      queue: queueSize > 0
        ? { count: queueSize, tooltip: `${queueSize} task${queueSize === 1 ? '' : 's'} waiting in 2-ready.` }
        : null
    };
  });

  /** `0` -> `0s`; `83410` -> `1m23s`; `3624000` -> `1h0m`. */
  private formatElapsed(ms: number): string {
    const totalSec = Math.floor(ms / 1000);
    if (totalSec < 60) return `${totalSec}s`;
    const m = Math.floor(totalSec / 60);
    const s = totalSec % 60;
    if (m < 60) return `${m}m${s.toString().padStart(2, '0')}s`;
    const h = Math.floor(m / 60);
    const mm = m % 60;
    return `${h}h${mm.toString().padStart(2, '0')}m`;
  }

  /**
   * Aggregated lane indicators rendered in collapsed-rail mode. The rail
   * stays useful for triage even when its cards are hidden: a running
   * count, a needs-input count (saved follow-ups waiting for the
   * orchestrator), an error/blocked count, and the CLI of the active
   * run when one exists.
   */
  readonly indicators = computed(() => {
    let running = 0;
    let needsInput = 0;
    let error = 0;
    let activeCli: string | null = null;
    for (const j of this.jobs()) {
      const status = j.execution?.status ?? null;
      if (status === 'running') {
        running++;
        if (!activeCli) activeCli = j.cliType ?? j.agent ?? null;
      } else if (status === 'failed' || status === 'cancelled' || status === 'stopped') {
        error++;
      }
      if (j.pendingIntent) needsInput++;
    }
    return { running, needsInput, error, activeCli };
  });

  cliIconFor(cli: string): string {
    if (cli === 'copilot' || cli === 'claude' || cli === 'codex' || cli === 'gemini') {
      return cliTypeIcon(cli);
    }
    return '🤖';
  }

  railTooltip(): string {
    const i = this.indicators();
    const lines: string[] = [];
    lines.push(`${this.title()} (${this.jobs().length} task${this.jobs().length === 1 ? '' : 's'})`);
    if (i.running) lines.push(`${i.running} running`);
    if (i.needsInput) lines.push(`${i.needsInput} pending follow-up`);
    if (i.error) lines.push(`${i.error} failed/stopped`);
    if (i.activeCli) lines.push(`Active CLI: ${i.activeCli}`);
    lines.push('');
    lines.push('Click to expand');
    return lines.join('\n');
  }

  isDragOver = false;
  dropIndex = -1;

  // Auto-scroll while a card is being dragged near a lane's vertical edges.
  // HTML5 drag suppresses wheel/keyboard scroll, so without this the user is
  // stuck at whatever scroll position the drag started in. With every lane
  // owning its own internal scroll container the auto-scroll target is the
  // .column__body under the cursor (or the window when no lane sits under
  // the cursor — e.g. the gap between groups). Active only between dragstart
  // and dragend on a card from this column.
  private autoScrollVelocity = 0;
  private autoScrollRaf: number | null = null;
  private autoScrollTarget: HTMLElement | Window = window;
  private readonly onAutoScrollDragOver = (e: DragEvent) => this.updateAutoScrollVelocity(e);
  private readonly onAutoScrollEnd = () => this.stopAutoScroll();

  canAddTask(): boolean {
    const s = this.state();
    return s === '1-preparation' || s === '2-ready';
  }

  isArchive(): boolean {
    // Accept both ADR-0025 and legacy archive lane names so a transitional
    // payload (legacy backend, new frontend) keeps rendering correctly.
    return this.state() === '7-archive' || this.state() === '6-archive';
  }

  /** ADR-0028: 3a-failed-pickup is the loud-not-archived lane. */
  isFailedPickup(): boolean {
    return this.state() === '3a-failed-pickup';
  }

  /** ADR-0025: 4-auto-review carries the lane-level "machine pass" controls. */
  isAutoReview(): boolean {
    return this.state() === '4-auto-review';
  }

  ngOnInit(): void {
    if (this.isAutoReview()) {
      this.autoReviewStatus.subscribe();
    }
  }

  ngOnDestroy(): void {
    if (this.isAutoReview()) {
      this.autoReviewStatus.release();
    }
  }

  /**
   * Every lane carries an info trigger: each one maps to a committed
   * concept doc under <c>docs/concept-docs/lane-*.md</c>, served by
   * <c>GET /api/concept-docs/{topic}</c> and shown in the lane-info
   * modal. Virtual sub-lanes (e.g. <c>2-ready-intake</c>, <c>4-review</c>)
   * collapse to their parent's doc. Returns <c>null</c> only for a state
   * with no doc, in which case the trigger is hidden.
   */
  readonly infoTopic = computed<string | null>(() => laneDocTopic(this.state()));

  /**
   * One-line live status string for the 4-auto-review lane header. Reads
   * the polled snapshot from {@link AutoReviewStatusStore}; falls back to
   * a static "waiting" message before the first tick completes so the
   * lane is never silent.
   */
  readonly autoReviewStatusLine = computed(() => {
    const s = this.autoReviewStatus.status();
    if (!s || !s.lastTickAt) {
      return 'Auto-review: waiting for first tick';
    }
    const delta = Math.max(0, Math.round((Date.now() - new Date(s.lastTickAt).getTime()) / 1000));
    const ago = delta < 60 ? `${delta}s ago` : `${Math.round(delta / 60)}m ago`;
    const pending = s.pending ?? 0;
    if (s.currentJob) {
      return `Reviewing ${s.currentJob}. Last tick: ${pending} queued · ${s.accept} accept · ${s.reissue} reissue · ${s.escalate} escalate (${ago})`;
    }
    return `Last tick: ${pending} queued · ${s.accept} accept · ${s.reissue} reissue · ${s.escalate} escalate (${ago})`;
  });

  /**
   * The ADR-0025 swim-lanes are now real columns; the in-column
   * subdivision only triggers for the legacy `4-review` payload (older
   * backend, newer frontend, or until the migration runs). The new
   * `4-auto-review` lane is itself the "machine" pass and the new
   * `5-human-review` lane is itself the "you" pass; they don't need an
   * extra in-column split.
   */
  isReview(): boolean {
    return this.state() === '4-review';
  }

  /**
   * Splits 4-review cards into two visually distinct sub-sections:
   *   - "Orchestrator review" holds cards with a non-null
   *     orchestratorVerdict (the orchestrator picked them up from a
   *     NEEDS_INPUT / NOOP / BLOCKED sentinel and decided reissue,
   *     escalate, or accept).
   *   - "Human review" holds the rest (clean DONE awaiting the user's
   *     accept).
   * The split is presentation-only; cards keep their underlying state
   * lane and drag-drop semantics. Reorder within the column is disabled
   * while subdivided so the swim-lanes stay coherent.
   */
  readonly reviewGroups = computed(() => groupReviewJobs(this.jobs()));

  canArchiveAll(): boolean {
    return this.state() === '6-completed' || this.state() === '5-completed';
  }

  readonly archiveVisible = computed(() => {
    if (!this.isArchive()) return [] as TaskInfo[];
    return [...this.jobs()]
      .sort((a, b) => (b.lastActivity ?? '').localeCompare(a.lastActivity ?? ''))
      .slice(0, ARCHIVE_VISIBLE_LIMIT);
  });

  readonly archiveOverflow = computed(() => {
    if (!this.isArchive()) return 0;
    return Math.max(0, this.jobs().length - ARCHIVE_VISIBLE_LIMIT);
  });

  readonly identityFor = (name: string) => projectIdentity(name);

  archiveTooltip(job: TaskInfo): string {
    const lines: string[] = [];
    lines.push(job.title || job.id);
    lines.push('');
    lines.push(`Project: ${job.projectName}`);
    if (job.agent) lines.push(`Agent: ${job.agent}${job.cliType ? ` (${job.cliType})` : ''}`);
    else if (job.cliType) lines.push(`CLI: ${job.cliType}`);
    if (job.model) lines.push(`Model: ${job.model}`);
    lines.push('');
    lines.push(`Created: ${this.formatLongDate(job.createdAt)}`);
    lines.push(`Last activity: ${this.formatLongDate(job.lastActivity)}`);
    if (job.commit) {
      lines.push('');
      lines.push(`Commit ${job.commit.shortSha}: ${this.firstLine(job.commit.message)}`);
      lines.push(`Files changed: ${job.commit.filesChanged}`);
    }
    return lines.join('\n');
  }

  private firstLine(s: string | null | undefined): string {
    if (!s) return '';
    const idx = s.indexOf('\n');
    return idx < 0 ? s : s.slice(0, idx);
  }

  formatLongDate(iso: string | null | undefined): string {
    if (!iso) return 'unknown';
    const d = new Date(iso);
    if (isNaN(d.getTime())) return 'unknown';
    const yyyy = d.getFullYear();
    const mm = String(d.getMonth() + 1).padStart(2, '0');
    const dd = String(d.getDate()).padStart(2, '0');
    const hh = String(d.getHours()).padStart(2, '0');
    const mi = String(d.getMinutes()).padStart(2, '0');
    return `${yyyy}-${mm}-${dd} ${hh}:${mi}`;
  }

  formatShortDate(iso: string | null | undefined): string {
    if (!iso) return '—';
    const d = new Date(iso);
    if (isNaN(d.getTime())) return '—';
    const yyyy = d.getFullYear();
    const mm = String(d.getMonth() + 1).padStart(2, '0');
    const dd = String(d.getDate()).padStart(2, '0');
    return `${yyyy}-${mm}-${dd}`;
  }

  onDragStart(event: DragEvent, job: TaskInfo) {
    event.dataTransfer?.setData('text/plain', JSON.stringify({ jobId: job.id, watchPath: job.watchPath, taskKey: job.taskKey }));
    event.dataTransfer?.setData('application/x-source-state', job.state);
    // Mark the host so the dimmed-while-dragging style applies. Released
    // on dragend (or drop) so the source eases back to full opacity
    // smoothly instead of snapping. Tracked imperatively so we can clear
    // it even when Angular re-renders the card into a different lane via
    // the optimistic move.
    const host = event.currentTarget as HTMLElement | null;
    if (host) {
      host.classList.add('drag-source');
      const clear = () => {
        host.classList.remove('drag-source');
        host.removeEventListener('dragend', clear);
      };
      host.addEventListener('dragend', clear);
    }
    this.startAutoScroll();
  }

  private startAutoScroll() {
    document.addEventListener('dragover', this.onAutoScrollDragOver);
    document.addEventListener('dragend', this.onAutoScrollEnd);
    document.addEventListener('drop', this.onAutoScrollEnd);
  }

  private updateAutoScrollVelocity(event: DragEvent) {
    const EDGE_PX = 80;
    const MAX_SPEED = 22;
    const x = event.clientX;
    const y = event.clientY;
    // Resolve which lane body (if any) the cursor is currently over. Scrolling
    // happens against that body rather than the window because the page no
    // longer scrolls vertically: each lane owns its scroll viewport.
    const laneBody = this.findLaneBodyAt(x, y);
    if (laneBody) {
      const rect = laneBody.getBoundingClientRect();
      let velocity = 0;
      if (y >= rect.top && y < rect.top + EDGE_PX) {
        velocity = -MAX_SPEED * (1 - (y - rect.top) / EDGE_PX);
      } else if (y > rect.bottom - EDGE_PX && y <= rect.bottom) {
        velocity = MAX_SPEED * (1 - (rect.bottom - y) / EDGE_PX);
      }
      this.autoScrollVelocity = velocity;
      this.autoScrollTarget = laneBody;
    } else {
      // Cursor is in the gap or off-board: fall back to window scrolling so
      // dragging works in detail-view contexts where the page itself can
      // scroll (e.g. .layout--focus).
      const h = window.innerHeight;
      let velocity = 0;
      if (y >= 0 && y < EDGE_PX) {
        velocity = -MAX_SPEED * (1 - y / EDGE_PX);
      } else if (y > h - EDGE_PX && y <= h) {
        velocity = MAX_SPEED * (1 - (h - y) / EDGE_PX);
      }
      this.autoScrollVelocity = velocity;
      this.autoScrollTarget = window;
    }
    if (this.autoScrollVelocity !== 0 && this.autoScrollRaf === null) {
      const tick = () => {
        if (this.autoScrollVelocity === 0) {
          this.autoScrollRaf = null;
          return;
        }
        const t = this.autoScrollTarget;
        if (t instanceof Window) {
          t.scrollBy(0, this.autoScrollVelocity);
        } else {
          t.scrollTop += this.autoScrollVelocity;
        }
        this.autoScrollRaf = requestAnimationFrame(tick);
      };
      this.autoScrollRaf = requestAnimationFrame(tick);
    }
  }

  private findLaneBodyAt(x: number, y: number): HTMLElement | null {
    // elementsFromPoint walks the topmost layer; we want the first ancestor
    // that is itself a vertical scroll container. The dragover event's
    // composedPath would also work but elementsFromPoint is more reliable
    // when the drag image hovers above the actual target.
    const els = typeof document.elementsFromPoint === 'function'
      ? document.elementsFromPoint(x, y) as Element[]
      : [];
    for (const el of els) {
      let cursor: Element | null = el;
      while (cursor && cursor !== document.body) {
        if (cursor instanceof HTMLElement && cursor.classList.contains('column__body')) {
          return cursor;
        }
        cursor = cursor.parentElement;
      }
    }
    return null;
  }

  private stopAutoScroll() {
    this.autoScrollVelocity = 0;
    if (this.autoScrollRaf !== null) {
      cancelAnimationFrame(this.autoScrollRaf);
      this.autoScrollRaf = null;
    }
    document.removeEventListener('dragover', this.onAutoScrollDragOver);
    document.removeEventListener('dragend', this.onAutoScrollEnd);
    document.removeEventListener('drop', this.onAutoScrollEnd);
  }

  onDragOver(event: DragEvent) {
    event.preventDefault();
    this.isDragOver = true;
  }

  onDragLeave(event: DragEvent) {
    // dragleave fires whenever the cursor moves between child elements; only
    // clear state when the cursor has actually left the column boundary.
    const related = event.relatedTarget as Node | null;
    const target = event.currentTarget as Node | null;
    if (related && target && (target as Element).contains(related)) return;
    this.isDragOver = false;
    this.dropIndex = -1;
  }

  onCardDragOver(event: DragEvent, index: number) {
    event.preventDefault();
    event.stopPropagation();
    this.dropIndex = index;
  }

  onCardDragLeave() {
    // Intentionally a no-op: dragleave on a drop-zone fires when entering an
    // adjacent zone or card and would cause the active indicator to flicker.
    // The column-level onDragLeave clears dropIndex when the cursor truly
    // leaves the column.
  }

  onCardDrop(event: DragEvent, index: number) {
    event.preventDefault();
    event.stopPropagation();
    this.isDragOver = false;
    this.dropIndex = -1;
    const payload = this.parsePayload(event.dataTransfer?.getData('text/plain'));
    const sourceState = event.dataTransfer?.getData('application/x-source-state');
    if (!payload) return;

    if (sourceState === this.state()) {
      this.performSameLaneReorder(payload.taskKey, index);
    } else {
      this.jobDrop.emit({
        jobId: payload.jobId,
        watchPath: payload.watchPath,
        targetState: this.state(),
        targetIndex: index
      });
    }
  }

  onDrop(event: DragEvent) {
    event.preventDefault();
    this.isDragOver = false;
    this.dropIndex = -1;
    const payload = this.parsePayload(event.dataTransfer?.getData('text/plain'));
    if (!payload) return;
    const sourceState = event.dataTransfer?.getData('application/x-source-state');
    if (sourceState === this.state()) {
      // Same-lane drop that missed the per-card drop-zone strips: the strips
      // are intentionally narrow (~14 px) so the user can't accidentally
      // reorder while drag-scrolling, but that makes "drag to the very top
      // of the lane" hard to land — the strip above the first card is a thin
      // ribbon and the cursor frequently ends up on the first card's body
      // instead. The drop then bubbled here and was silently dropped, or it
      // landed on strip i=1 and the dragged card ended at order 2 instead
      // of order 1. The sustainable fix is to compute the drop slot from
      // the cursor Y vs each card's midpoint: any drop above the first
      // card's midpoint produces order 1, any drop below the last card's
      // midpoint produces the largest order, and drops on a sibling card
      // route by which half the cursor is in. The card-vanish regression
      // (lane-reorder-drop-on-card.spec.ts) is preserved because we now
      // emit jobReorder (not jobDrop) for same-lane drops.
      //
      // Reorder stays suppressed when reorder is disabled or when the lane
      // renders the legacy 4-review subdivision (which intentionally
      // disables reorder so the orchestrator/human swim-lanes stay coherent).
      if (this.reorderDisabled() || this.isReview()) return;
      const slot = this.computeDropSlotFromCursor(event);
      this.performSameLaneReorder(payload.taskKey, slot);
      return;
    }
    // Cross-lane drop on the column body (missed the per-strip drop zones,
    // or the column has none because it renders a subdivided lane). Use
    // the same cursor-vs-card-midpoint slot the same-lane path uses so the
    // moved card lands where the user released, not at a position derived
    // from its stale source-lane order. Lanes that disable reorder (review
    // subdivision) still get a deterministic slot via the trailing index.
    const targetIndex = (this.reorderDisabled() || this.isReview())
      ? this.jobs().length
      : this.computeDropSlotFromCursor(event);
    this.jobDrop.emit({
      jobId: payload.jobId,
      watchPath: payload.watchPath,
      targetState: this.state(),
      targetIndex
    });
  }

  /**
   * Find the insertion slot (0..jobs.length) that corresponds to the
   * cursor's vertical position. Slot `i` means "insert before card i"; slot
   * `jobs.length` means "append after the last card". Cards are queried
   * from the column root so the result reflects the actual rendered
   * positions (gap, padding, scroll offset all baked into the rect).
   */
  private computeDropSlotFromCursor(event: DragEvent): number {
    const columnEl = event.currentTarget as HTMLElement | null;
    if (!columnEl) return this.jobs().length;
    const cards = Array.from(columnEl.querySelectorAll('app-job-card')) as HTMLElement[];
    if (cards.length === 0) return 0;
    const cursorY = event.clientY;
    for (let i = 0; i < cards.length; i++) {
      const rect = cards[i].getBoundingClientRect();
      const mid = rect.top + rect.height / 2;
      if (cursorY < mid) return i;
    }
    return cards.length;
  }

  /**
   * Apply a same-lane reorder to the column's job list and emit the
   * resulting order. `slot` uses the drop-zone-strip convention (0 means
   * "before the first card", jobs.length means "after the last"). When the
   * slot would not actually move the card (drop on its own row or the
   * adjacent boundary), the call is a no-op so the optimistic-paint layer
   * doesn't churn for an empty reorder.
   */
  private performSameLaneReorder(taskKey: string, slot: number): void {
    const currentJobs = this.jobs().map(j => ({ jobId: j.id, watchPath: j.watchPath, taskKey: j.taskKey }));
    const fromIndex = currentJobs.findIndex(job => job.taskKey === taskKey);
    if (fromIndex < 0) return;
    if (slot === fromIndex || slot === fromIndex + 1) return;
    const [movedJob] = currentJobs.splice(fromIndex, 1);
    const insertAt = slot > fromIndex ? slot - 1 : slot;
    currentJobs.splice(insertAt, 0, movedJob);
    this.jobReorder.emit({
      state: this.state(),
      jobs: currentJobs.map(job => ({ jobId: job.jobId, watchPath: job.watchPath }))
    });
  }

  private parsePayload(rawPayload?: string): { jobId: string; watchPath: string; taskKey: string } | null {
    if (!rawPayload) return null;
    try {
      const payload = JSON.parse(rawPayload) as { jobId?: string; watchPath?: string; taskKey?: string };
      if (!payload.jobId || !payload.watchPath || !payload.taskKey) return null;
      return { jobId: payload.jobId, watchPath: payload.watchPath, taskKey: payload.taskKey };
    } catch {
      return null;
    }
  }
}
