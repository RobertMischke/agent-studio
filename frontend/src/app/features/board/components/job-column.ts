import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { JobInfo, JobOrderItem } from '../../../models/job.model';
import { JobCardComponent } from './job-card/job-card.component';
import { projectIdentity } from '../../../services/project-identity.util';
import { cliTypeIcon } from '../../../services/format.util';
import { InstantTooltipDirective } from '../../../directives/instant-tooltip.directive';
import { groupReviewJobs } from './review-grouping.util';
import { AutoReviewStatusStore } from '../../../services/auto-review-status.store';
import { InfoButtonComponent } from '../../../components/info-button/info-button.component';

const ARCHIVE_VISIBLE_LIMIT = 20;

/**
 * Cycle 7c2: virtualization kicks in once a non-archive, non-review
 * lane crosses VIRTUAL_SCROLL_THRESHOLD cards. Below that, the
 * existing per-card drop-zone path stays in place so day-to-day
 * reorder UX is unchanged. The estimate VIRTUAL_ITEM_SIZE_PX is the
 * average non-compact card height; CDK's FixedSize strategy uses it
 * to compute the scroll buffer + which range to render. Real cards
 * may be slightly taller (extra commit chips, longer titles) - the
 * size estimate just needs to be in the right ballpark; mismatches
 * cause a small visual jump on scroll, never wrong content.
 */
const VIRTUAL_SCROLL_THRESHOLD = 50;
const VIRTUAL_ITEM_SIZE_PX = 160;

@Component({
  selector: 'app-job-column',
  standalone: true,
  imports: [JobCardComponent, InstantTooltipDirective, InfoButtonComponent, ScrollingModule],
  // Cycle 7b: OnPush. The board mounts ~10 columns and re-renders the
  // full @for of cards every CD pass under Default. JobCard is already
  // OnPush; promoting the column propagates that benefit upward so a
  // poll tick that didn't change THIS lane's jobs() input doesn't
  // walk the lane's children either. Inputs are signal-based so OnPush
  // marks dirty correctly without needing markForCheck.
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './job-column.html',
  styleUrl: './job-column.scss'
})
export class JobColumnComponent implements OnInit, OnDestroy {
  private readonly autoReviewStatus = inject(AutoReviewStatusStore);

  // Template literals exposed as instance fields so the component
  // template can bind them without going through computeds.
  readonly VIRTUAL_ITEM_SIZE_PX = VIRTUAL_ITEM_SIZE_PX;

  readonly title = input.required<string>();
  readonly icon = input<string>('');
  readonly state = input.required<string>();
  readonly jobs = input.required<JobInfo[]>();
  readonly reorderDisabled = input<boolean>(false);
  readonly collapsed = input<boolean>(false);
  readonly compact = input<boolean>(false);
  readonly archiving = input<boolean>(false);

  /** True when this lane should render its cards through CDK virtual
   *  scrolling instead of the default @for. Archive and review have
   *  their own templates and stay on the legacy path. */
  readonly useVirtualScroll = computed(() =>
    !this.isArchive()
    && !this.isReview()
    && this.jobs().length > VIRTUAL_SCROLL_THRESHOLD
  );

  /** trackBy for *cdkVirtualFor: stable key per row keeps DOM nodes
   *  reused on lane updates instead of full re-create. */
  readonly trackByJobKey = (_: number, job: JobInfo) => job.jobKey;
  readonly jobClick = output<JobInfo>();
  readonly jobDrop = output<{ jobId: string; watchPath: string; targetState: string }>();
  readonly jobReorder = output<{ state: string; jobs: JobOrderItem[] }>();
  readonly jobDeleteRequest = output<JobInfo>();
  readonly addTask = output<string>();
  readonly archiveAll = output<void>();
  readonly collapseToggle = output<void>();

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

  // Auto-scroll the page while a card is being dragged near the viewport edges.
  // HTML5 drag suppresses wheel/keyboard scroll, so without this the user is
  // stuck at whatever scroll position the drag started in. Active only between
  // dragstart and dragend on a card from this column.
  private autoScrollVelocity = 0;
  private autoScrollRaf: number | null = null;
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
   * Selective info-button placement (per the design contract): only
   * lanes whose semantics are non-obvious get the small "i" trigger.
   * Returns the topic id under <c>docs/concept-docs/</c> for the
   * current lane, or <c>null</c> when no concept doc exists for it.
   * Backlog / Ready / Done deliberately have nothing here.
   */
  readonly infoTopic = computed<string | null>(() => {
    switch (this.state()) {
      case '4-auto-review': return 'lane-4-auto-review';
      case '3-progress':    return 'lane-3-progress';
      default:              return null;
    }
  });

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
    if (!this.isArchive()) return [] as JobInfo[];
    return [...this.jobs()]
      .sort((a, b) => (b.lastActivity ?? '').localeCompare(a.lastActivity ?? ''))
      .slice(0, ARCHIVE_VISIBLE_LIMIT);
  });

  readonly archiveOverflow = computed(() => {
    if (!this.isArchive()) return 0;
    return Math.max(0, this.jobs().length - ARCHIVE_VISIBLE_LIMIT);
  });

  readonly identityFor = (name: string) => projectIdentity(name);

  archiveTooltip(job: JobInfo): string {
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

  onDragStart(event: DragEvent, job: JobInfo) {
    event.dataTransfer?.setData('text/plain', JSON.stringify({ jobId: job.id, watchPath: job.watchPath, jobKey: job.jobKey }));
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
    const y = event.clientY;
    const h = window.innerHeight;
    let velocity = 0;
    if (y >= 0 && y < EDGE_PX) {
      velocity = -MAX_SPEED * (1 - y / EDGE_PX);
    } else if (y > h - EDGE_PX && y <= h) {
      velocity = MAX_SPEED * (1 - (h - y) / EDGE_PX);
    }
    this.autoScrollVelocity = velocity;
    if (velocity !== 0 && this.autoScrollRaf === null) {
      const tick = () => {
        if (this.autoScrollVelocity === 0) {
          this.autoScrollRaf = null;
          return;
        }
        window.scrollBy(0, this.autoScrollVelocity);
        this.autoScrollRaf = requestAnimationFrame(tick);
      };
      this.autoScrollRaf = requestAnimationFrame(tick);
    }
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
      this.performSameLaneReorder(payload.jobKey, index);
    } else {
      this.jobDrop.emit({ jobId: payload.jobId, watchPath: payload.watchPath, targetState: this.state() });
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
      this.performSameLaneReorder(payload.jobKey, slot);
      return;
    }
    this.jobDrop.emit({ jobId: payload.jobId, watchPath: payload.watchPath, targetState: this.state() });
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
  private performSameLaneReorder(jobKey: string, slot: number): void {
    const currentJobs = this.jobs().map(j => ({ jobId: j.id, watchPath: j.watchPath, jobKey: j.jobKey }));
    const fromIndex = currentJobs.findIndex(job => job.jobKey === jobKey);
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

  private parsePayload(rawPayload?: string): { jobId: string; watchPath: string; jobKey: string } | null {
    if (!rawPayload) return null;
    try {
      const payload = JSON.parse(rawPayload) as { jobId?: string; watchPath?: string; jobKey?: string };
      if (!payload.jobId || !payload.watchPath || !payload.jobKey) return null;
      return { jobId: payload.jobId, watchPath: payload.watchPath, jobKey: payload.jobKey };
    } catch {
      return null;
    }
  }
}
