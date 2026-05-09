import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { JobInfo, JobOrderItem } from '../models/job.model';
import { JobCardComponent } from './job-card';
import { projectIdentity } from '../services/project-identity.util';
import { cliTypeIcon } from '../services/format.util';
import { InstantTooltipDirective } from '../directives/instant-tooltip.directive';
import { groupReviewJobs } from './review-grouping.util';
import { AutoReviewStatusStore } from '../services/auto-review-status.store';
import { InfoButtonComponent } from './info-button/info-button.component';

const ARCHIVE_VISIBLE_LIMIT = 20;

@Component({
  selector: 'app-job-column',
  standalone: true,
  imports: [JobCardComponent, InstantTooltipDirective, InfoButtonComponent],
  // Cycle 7b: OnPush. The board mounts ~10 columns and re-renders the
  // full @for of cards every CD pass under Default. JobCard is already
  // OnPush; promoting the column propagates that benefit upward so a
  // poll tick that didn't change THIS lane's jobs() input doesn't
  // walk the lane's children either. Inputs are signal-based so OnPush
  // marks dirty correctly without needing markForCheck.
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (collapsed()) {
      <button type="button"
              class="column-rail"
              [class.column-rail--dragover]="isDragOver"
              [attr.data-testid]="'lane-rail-' + state()"
              [attr.data-state]="state()"
              [appTip]="railTooltip()"
              (click)="collapseToggle.emit()"
              (dragover)="onDragOver($event)"
              (dragleave)="onDragLeave($event)"
              (drop)="onDrop($event)">
          <span class="column-rail__icon" aria-hidden="true">{{ icon() }}</span>
          <span class="column-rail__count" data-testid="lane-rail-count">{{ jobs().length }}</span>
          <span class="column-rail__title">{{ title() }}</span>
          <span class="column-rail__indicators" aria-hidden="true">
            @if (indicators().running > 0) {
              <span class="column-rail__dot column-rail__dot--running"
                    [attr.data-testid]="'lane-rail-running-' + state()"
                    [attr.data-count]="indicators().running"></span>
            }
            @if (indicators().needsInput > 0) {
              <span class="column-rail__dot column-rail__dot--needs-input"
                    [attr.data-testid]="'lane-rail-needs-input-' + state()"
                    [attr.data-count]="indicators().needsInput">!</span>
            }
            @if (indicators().error > 0) {
              <span class="column-rail__dot column-rail__dot--error"
                    [attr.data-testid]="'lane-rail-error-' + state()"
                    [attr.data-count]="indicators().error">×</span>
            }
            @if (indicators().activeCli; as cli) {
              <span class="column-rail__cli"
                    [attr.data-testid]="'lane-rail-cli-' + state()"
                    [attr.data-cli]="cli">{{ cliIconFor(cli) }}</span>
            }
          </span>
          <span class="column-rail__expand" aria-hidden="true">›</span>
      </button>
    } @else {
    <div class="column"
         [class.column--dragover]="isDragOver"
         [class.column--archive]="isArchive()"
         [class.column--failed-pickup]="isFailedPickup()"
         [attr.data-testid]="'lane-' + state()"
         [attr.data-state]="state()"
         (dragover)="onDragOver($event)"
         (dragleave)="onDragLeave($event)"
         (drop)="onDrop($event)">
      <div class="column__header">
        <span class="column__icon">{{ icon() }}</span>
        @if (isFailedPickup()) {
          <span class="column__amber-dot"
                aria-hidden="true"
                data-testid="failed-pickup-dot"></span>
        }
        <h2 class="column__title">{{ title() }}</h2>
        <span class="column__count">{{ jobs().length }}</span>
        @if (infoTopic(); as topic) {
          <app-info-button [topic]="topic" />
        }
        @if (canArchiveAll()) {
          <button type="button"
                  class="column__archive-all"
                  data-testid="archive-all-btn"
                  [appTip]="'Move all completed tasks to Archive'"
                  (click)="archiveAll.emit()">
            ⬇ Archive all
          </button>
        }
        @if (!isFailedPickup()) {
          <button type="button"
                  class="column__collapse"
                  [attr.data-testid]="'lane-collapse-' + state()"
                  [appTip]="'Collapse ' + title() + ' lane'"
                  (click)="collapseToggle.emit()">‹</button>
        }
      </div>
      @if (isAutoReview()) {
        <div class="column__auto-review-status"
             data-testid="auto-review-status">
          {{ autoReviewStatusLine() }}
        </div>
      }
      <div class="column__body">
        @if (isArchive()) {
          @for (job of archiveVisible(); track job.jobKey) {
            <button type="button"
                    class="archive-row"
                    [attr.data-testid]="'archive-row'"
                    [style.--project-color]="identityFor(job.projectName).color"
                    [style.--project-on]="identityFor(job.projectName).onColor"
                    [appTip]="archiveTooltip(job)"
                    (click)="jobClick.emit(job)">
              <span class="archive-row__date">{{ formatShortDate(job.lastActivity) }}</span>
              <span class="archive-row__disk"
                    [attr.aria-label]="job.projectName">{{ identityFor(job.projectName).initial }}</span>
              <span class="archive-row__title">{{ job.title || job.id }}</span>
            </button>
          }
          @if (jobs().length === 0) {
            <div class="column__empty">No archived jobs</div>
          } @else if (archiveOverflow() > 0) {
            <div class="archive-overflow">
              + {{ archiveOverflow() }} more in <code>6-archive/</code> folder
            </div>
          }
        } @else if (isReview()) {
          @for (group of reviewGroups(); track group.kind) {
            <section class="column__subsection"
                     [class]="'column__subsection--' + group.kind"
                     [attr.data-testid]="'review-subsection-' + group.kind">
              <h3 class="column__subsection-title">
                <span class="column__subsection-icon" aria-hidden="true">{{ group.icon }}</span>
                <span>{{ group.label }}</span>
                <span class="column__subsection-count">{{ group.jobs.length }}</span>
              </h3>
              @for (job of group.jobs; track job.jobKey) {
                <app-job-card
                  [job]="job"
                  [compact]="compact()"
                  (click)="jobClick.emit(job)"
                  (deleteRequested)="jobDeleteRequest.emit($event)"
                  draggable="true"
                  (dragstart)="onDragStart($event, job)" />
              }
              @if (group.jobs.length === 0) {
                <div class="column__empty column__empty--subsection">No jobs</div>
              }
            </section>
          }
        } @else {
          @for (job of jobs(); track job.jobKey; let i = $index) {
            @if (!reorderDisabled()) {
              <div class="column__drop-zone"
                   [class.column__drop-zone--active]="dropIndex === i"
                   (dragover)="onCardDragOver($event, i)"
                   (dragleave)="onCardDragLeave()"
                   (drop)="onCardDrop($event, i)">
              </div>
            }
            <app-job-card
              [job]="job"
              [compact]="compact()"
              (click)="jobClick.emit(job)"
              draggable="true"
              (dragstart)="onDragStart($event, job)" />
          }
          @if (!reorderDisabled()) {
            <div class="column__drop-zone column__drop-zone--last"
                 [class.column__drop-zone--active]="dropIndex === jobs().length"
                 (dragover)="onCardDragOver($event, jobs().length)"
                 (dragleave)="onCardDragLeave()"
                 (drop)="onCardDrop($event, jobs().length)">
            </div>
          }
          @if (jobs().length === 0) {
            <div class="column__empty">No jobs</div>
          }
          @if (canAddTask()) {
            <button type="button" class="column__add" (click)="addTask.emit(state())">
              <span class="column__add-icon">＋</span>
              <span>Add task</span>
            </button>
          }
        }
      </div>
    </div>
    }
  `,
  styles: [`
    .column {
      background: var(--column-bg, #181825);
      border-radius: 16px;
      padding: 16px;
      /*
       * Columns share whatever horizontal space the dashboard has left
       * after gaps and group padding. The 220px floor matches the
       * kanban-board-design-spec-mockup-first contract; below that the
       * dashboard's overflow-x: auto kicks in. flex: 1 1 220px lets
       * lanes grow past their floor so the board fills 100% of the
       * viewport instead of leaving empty space at the right.
       */
      min-width: 220px;
      flex: 1 1 220px;
      display: flex;
      flex-direction: column;
      gap: 12px;
      transition: outline 0.15s;
      /*
       * CSS containment: scrolling a single lane's body or polling a
       * single lane's status (4-auto-review header line) must not
       * trigger layout/paint work in sibling lanes. The "layout" scope
       * keeps descendants' size/position changes from invalidating
       * ancestors; the "paint" scope clips the column's painting to
       * its own border box. The default style/size scopes are
       * intentionally left untouched so descendant counters and
       * viewport units still resolve.
       *
       * This is the perf intervention that brings the long-task
       * budget during a dense-board 5 s scroll below the 50 ms
       * acceptance criterion in the lane-overlap regression spec.
       */
      contain: layout paint;
    }
    /* Drag-over signal: outline ring only. We deliberately do NOT tint the
       column background because a transient overlay that snaps off on drop
       reads as a "flash" together with the drop-zone glow underneath the
       landed card. Motion rule (docs/design-principles.md): drag-and-drop
       never changes brightness; only opacity (drop-zone) and transform. */
    .column--dragover {
      outline: 2px solid rgba(99, 102, 241, 0.6);
      outline-offset: -2px;
    }
    /* ADR-0028: 3a-failed-pickup is the only lane that uses an outline tint
       on the column itself. 1 px amber per the kanban-board-design taxonomy.
       Pickup failures must remain visible; collapse is suppressed in the
       template so a non-empty failed-pickup lane cannot be hidden. */
    .column--failed-pickup {
      border: 1px solid rgba(245, 158, 11, 0.55);
    }
    .column--failed-pickup .column__title { color: #fbbf24; }
    .column__amber-dot {
      width: 12px;
      height: 12px;
      border-radius: 999px;
      background: #f59e0b;
      box-shadow: 0 0 0 2px rgba(245, 158, 11, 0.20);
      flex-shrink: 0;
      display: inline-block;
    }
    .column__header {
      display: flex;
      align-items: center;
      gap: 8px;
      padding-bottom: 8px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
      /*
       * Clip header content to the column. With min-width: 200 on
       * .column the header's natural fixed-size children (icon, count,
       * Archive-all, collapse) sum close to 170px, leaving little room
       * for the title. Without this clamp the trailing collapse button
       * overflows into the neighbouring lane and intercepts pointer
       * events meant for that lane (caught by kanban-lane-grouping
       * spec). Pair with min-width: 0 + ellipsis on the title so it
       * shrinks instead.
       */
      min-width: 0;
      overflow: hidden;
    }
    .column__icon { font-size: 18px; flex-shrink: 0; }
    .column__title {
      margin: 0;
      font-size: 14px;
      font-weight: 600;
      color: #e2e8f0;
      flex: 1 1 0;
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .column__count {
      background: rgba(255,255,255,0.08);
      border-radius: 10px;
      padding: 2px 8px;
      font-size: 12px;
      color: #94a3b8;
    }
    .column__body {
      display: flex;
      flex-direction: column;
      gap: 8px;
      flex: 1;
    }
    .column__empty {
      text-align: center;
      color: #4a5568;
      font-size: 13px;
      padding: 24px 0;
    }
    .column__empty--subsection {
      padding: 8px 0;
      font-size: 12px;
    }
    /* 4-review swim-lane subdivisions: orchestrator-decided cards visually
       separate from human-review cards so the user sees at a glance which
       cards the orchestrator already triaged. */
    .column__subsection {
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding: 8px;
      border-radius: 10px;
      background: rgba(255, 255, 255, 0.02);
      border: 1px solid rgba(255, 255, 255, 0.04);
    }
    .column__subsection--orchestrator {
      background: rgba(139, 92, 246, 0.06);
      border-color: rgba(139, 92, 246, 0.20);
    }
    .column__subsection--human {
      background: rgba(56, 189, 248, 0.04);
      border-color: rgba(56, 189, 248, 0.16);
    }
    .column__subsection-title {
      margin: 0 0 2px;
      display: flex;
      align-items: center;
      gap: 6px;
      font-size: 11px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: #cbd5e1;
    }
    .column__subsection--orchestrator .column__subsection-title { color: #c4b5fd; }
    .column__subsection--human        .column__subsection-title { color: #7dd3fc; }
    .column__subsection-icon { font-size: 13px; line-height: 1; }
    .column__subsection-count {
      margin-left: auto;
      background: rgba(255,255,255,0.08);
      color: #cbd5e1;
      border-radius: 10px;
      padding: 1px 6px;
      font-size: 10px;
      font-weight: 700;
      font-variant-numeric: tabular-nums;
      min-width: 20px;
      text-align: center;
    }
    /* The drop-zone occupies just 2px of layout space but extends its hit
       target via padding + negative margins, eating into the surrounding
       8px flex gaps. This makes it ~14px tall to the cursor without
       changing the visual rhythm of the column. */
    .column__drop-zone {
      position: relative;
      height: 2px;
      padding: 6px 0;
      margin: -6px 0;
      flex-shrink: 0;
    }
    .column__drop-zone::before {
      content: '';
      position: absolute;
      left: 4px;
      right: 4px;
      top: 50%;
      height: 3px;
      transform: translateY(-50%);
      border-radius: 2px;
      background: rgba(99, 102, 241, 0.9);
      opacity: 0;
      transition: opacity 0.12s ease;
      pointer-events: none;
    }
    /* Active drop-zone fades in via opacity only — no box-shadow glow.
       The glow used to leak beyond the strip's bounds and lingered for
       ~80ms after drop while it transitioned out, registering as a
       "brightness flash" underneath the landed card. */
    .column__drop-zone--active::before {
      opacity: 1;
    }
    .column__add {
      margin-top: 4px;
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 6px;
      width: 100%;
      background: rgba(139, 92, 246, 0.08);
      border: 1px dashed rgba(139, 92, 246, 0.35);
      color: #a78bfa;
      padding: 10px 12px;
      border-radius: 12px;
      cursor: pointer;
      font-size: 13px;
      font-weight: 500;
      transition: background 0.15s, border-color 0.15s, color 0.15s;
    }
    .column__add:hover {
      background: rgba(139, 92, 246, 0.18);
      border-color: rgba(139, 92, 246, 0.6);
      color: #c4b5fd;
    }
    .column__add-icon {
      font-size: 16px;
      line-height: 1;
    }
    .column__auto-review-status {
      margin-top: -4px;
      padding: 4px 8px;
      border-radius: 8px;
      background: rgba(139, 92, 246, 0.05);
      border: 1px solid rgba(139, 92, 246, 0.15);
      color: #c4b5fd;
      font-size: 11px;
      line-height: 1.4;
      font-variant-numeric: tabular-nums;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .column__archive-all {
      background: rgba(100, 116, 139, 0.12);
      border: 1px solid rgba(100, 116, 139, 0.3);
      color: #94a3b8;
      padding: 3px 8px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 11px;
      font-weight: 500;
      white-space: nowrap;
      transition: background 0.15s, border-color 0.15s, color 0.15s;
    }
    .column__archive-all:hover {
      background: rgba(100, 116, 139, 0.25);
      border-color: rgba(100, 116, 139, 0.55);
      color: #cbd5e1;
    }
    .column__collapse {
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.08);
      color: #94a3b8;
      width: 22px;
      height: 22px;
      padding: 0;
      border-radius: 6px;
      cursor: pointer;
      font-size: 14px;
      line-height: 1;
      display: grid;
      place-items: center;
      transition: background 0.15s, color 0.15s, border-color 0.15s;
    }
    .column__collapse:hover {
      background: rgba(255,255,255,0.10);
      border-color: rgba(255,255,255,0.18);
      color: #e2e8f0;
    }
    /*
     * Collapsed-lane rail. Same vertical rhythm as a full column so the
     * board stays aligned, but only ~36px wide. The rail itself is the
     * drop target and the click target for re-expansion. Indicators live
     * in a vertical strip in the middle so the user still sees task
     * count, active runs, needs-input flags, and errors at a glance.
     */
    .column-rail {
      background: var(--column-bg, #181825);
      border: 1px solid rgba(255,255,255,0.04);
      border-radius: 14px;
      width: 36px;
      min-width: 36px;
      flex: 0 0 36px;
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 10px;
      padding: 12px 4px 10px;
      cursor: pointer;
      color: #cbd5e1;
      transition: outline 0.15s, background 0.15s;
      /* Same containment rationale as .column: a rail's running-pulse
         animation must not invalidate sibling lanes. */
      contain: layout paint;
    }
    .column-rail:hover {
      background: #1a1a2b;
    }
    .column-rail--dragover {
      outline: 2px solid rgba(99, 102, 241, 0.6);
      outline-offset: -2px;
    }
    .column-rail__icon { font-size: 16px; line-height: 1; }
    .column-rail__count {
      background: rgba(255,255,255,0.08);
      color: #cbd5e1;
      border-radius: 10px;
      padding: 2px 6px;
      font-size: 11px;
      font-weight: 700;
      font-variant-numeric: tabular-nums;
      min-width: 22px;
      text-align: center;
    }
    .column-rail__title {
      writing-mode: vertical-rl;
      transform: rotate(180deg);
      font-size: 11px;
      font-weight: 600;
      color: #94a3b8;
      letter-spacing: 0.06em;
      text-transform: uppercase;
      margin: 4px 0 6px;
      flex: 1 1 auto;
      white-space: nowrap;
    }
    .column-rail__indicators {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 4px;
    }
    .column-rail__dot {
      width: 14px;
      height: 14px;
      border-radius: 999px;
      display: grid;
      place-items: center;
      font-size: 9px;
      font-weight: 800;
      line-height: 1;
    }
    .column-rail__dot--running {
      background: rgba(56,189,248,0.20);
      border: 1px solid rgba(56,189,248,0.55);
      color: #7dd3fc;
      animation: column-rail-pulse 1.4s ease-in-out infinite;
    }
    .column-rail__dot--running::after {
      content: '';
      width: 6px;
      height: 6px;
      border-radius: 999px;
      background: #7dd3fc;
    }
    .column-rail__dot--needs-input {
      background: rgba(252, 211, 77, 0.18);
      border: 1px solid rgba(252, 211, 77, 0.55);
      color: #fcd34d;
    }
    .column-rail__dot--error {
      background: rgba(244, 63, 94, 0.20);
      border: 1px solid rgba(244, 63, 94, 0.55);
      color: #fda4af;
    }
    @keyframes column-rail-pulse {
      0%, 100% { box-shadow: 0 0 0 0 rgba(56,189,248,0.55); }
      50% { box-shadow: 0 0 0 4px rgba(56,189,248,0.0); }
    }
    .column-rail__cli {
      font-size: 12px;
      line-height: 1;
      filter: saturate(1.1);
    }
    .column-rail__expand {
      color: #64748b;
      font-size: 14px;
      line-height: 1;
      margin-top: auto;
    }
    .column-rail:hover .column-rail__expand { color: #cbd5e1; }
    .column--archive .column__body { gap: 2px; }
    .archive-row {
      display: grid;
      grid-template-columns: auto auto 1fr;
      gap: 8px;
      align-items: baseline;
      width: 100%;
      text-align: left;
      background: transparent;
      border: 0;
      border-bottom: 1px solid rgba(255,255,255,0.04);
      color: #cbd5e1;
      padding: 4px 6px;
      font-size: 12px;
      line-height: 1.3;
      cursor: pointer;
      transition: background 0.12s, color 0.12s;
    }
    .archive-row:hover { background: rgba(255,255,255,0.05); color: #f1f5f9; }
    .archive-row__date {
      font-family: 'Consolas', monospace;
      color: #64748b;
      font-size: 11px;
      font-variant-numeric: tabular-nums;
    }
    .archive-row__disk {
      display: inline-grid;
      place-items: center;
      width: 14px;
      height: 14px;
      border-radius: 999px;
      background: var(--project-color, #8b5cf6);
      color: var(--project-on, #0b1020);
      font-size: 9px;
      font-weight: 800;
      flex: 0 0 auto;
    }
    .archive-row__title {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .archive-overflow {
      margin-top: 8px;
      padding: 6px 8px;
      font-size: 11px;
      color: #64748b;
      border-top: 1px dashed rgba(255,255,255,0.08);
    }
    .archive-overflow code {
      background: rgba(255,255,255,0.06);
      padding: 1px 5px;
      border-radius: 4px;
      font-size: 10px;
    }
    @media (prefers-reduced-motion: reduce) {
      .column { transition: none; }
      .column__drop-zone::before { transition: none; }
      .column-rail { transition: none; }
    }
  `]
})
export class JobColumnComponent implements OnInit, OnDestroy {
  private readonly autoReviewStatus = inject(AutoReviewStatusStore);

  readonly title = input.required<string>();
  readonly icon = input<string>('');
  readonly state = input.required<string>();
  readonly jobs = input.required<JobInfo[]>();
  readonly reorderDisabled = input<boolean>(false);
  readonly collapsed = input<boolean>(false);
  readonly compact = input<boolean>(false);
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
    if (s.currentJob) {
      return `Reviewing ${s.currentJob}. Last tick: ${s.accept} accept · ${s.reissue} reissue · ${s.escalate} escalate (${ago})`;
    }
    return `Last tick: ${s.accept} accept · ${s.reissue} reissue · ${s.escalate} escalate (${ago})`;
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
      // Reorder within same column
      const currentJobs = this.jobs().map(j => ({ jobId: j.id, watchPath: j.watchPath, jobKey: j.jobKey }));
      const fromIndex = currentJobs.findIndex(job => job.jobKey === payload.jobKey);
      if (fromIndex >= 0) {
        const [movedJob] = currentJobs.splice(fromIndex, 1);
        const insertAt = index > fromIndex ? index - 1 : index;
        currentJobs.splice(insertAt, 0, movedJob);
      }
      this.jobReorder.emit({
        state: this.state(),
        jobs: currentJobs.map(job => ({ jobId: job.jobId, watchPath: job.watchPath }))
      });
    } else {
      // Cross-column move
      this.jobDrop.emit({ jobId: payload.jobId, watchPath: payload.watchPath, targetState: this.state() });
    }
  }

  onDrop(event: DragEvent) {
    event.preventDefault();
    this.isDragOver = false;
    this.dropIndex = -1;
    const payload = this.parsePayload(event.dataTransfer?.getData('text/plain'));
    if (!payload) return;
    // The card drop-zone strips are intentionally narrow (~14 px hit target);
    // when the user releases the cursor over an actual card the drop bubbles
    // up to this column-level handler. If the card already lives in this
    // lane, treat it as a within-lane drop and emit nothing — emitting
    // `jobDrop` with `targetState === sourceState` would route through the
    // optimistic-move path, which removes the card from its lane and never
    // re-adds it (same fromLane/toLane case), making it disappear visually
    // until the next polling tick repaints. Cross-lane drops still flow
    // through the same path.
    const sourceState = event.dataTransfer?.getData('application/x-source-state');
    if (sourceState === this.state()) return;
    this.jobDrop.emit({ jobId: payload.jobId, watchPath: payload.watchPath, targetState: this.state() });
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
