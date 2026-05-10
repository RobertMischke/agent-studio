import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import type { JobInfo } from '../../../../models/job.model';
import type { RunCommitInfo, RunRecord } from '../../../../features/run-timeline';
import { JobService } from '../../../../services/job.service';

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
 * from RunTimelinePollService and the commits from JobService on
 * demand. That keeps the polling cadence in one place and avoids
 * stale per-card state when the timeline updates.
 */
@Component({
  selector: 'app-run-timeline',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (visibleRuns().length === 0) {
      <div class="run-timeline__empty">No CLI runs yet — start the task to populate the timeline.</div>
    } @else {
      <div class="run-timeline">
        <!--
          Chat-first redesign: runs collapse to a compact icon row by
          default. Each square icon represents one CLI invocation; the
          colour conveys status (green = ok, red = fail, blue = running,
          amber = stopped/cancelled). Clicking an icon expands that run's
          full card inline below — same data the verbose timeline showed,
          one click away. The aggregate summary stays inline so the user
          knows how many runs there are without expanding anything.
        -->
        <div class="run-timeline__header run-timeline__header--icons">
          <span class="run-timeline__title">Runs</span>
          <div class="run-timeline__icons" role="list" data-testid="runs-icon-row">
            @for (r of iconRuns(); track r.index) {
              <button type="button"
                      class="run-timeline__icon"
                      role="listitem"
                      [class.run-timeline__icon--active]="expandedIndex() === r.index"
                      [attr.data-status]="r.status"
                      [attr.data-testid]="'run-icon-' + r.index"
                      [attr.aria-label]="'Run #' + r.index + ' ' + statusLabel(r)"
                      [title]="iconTooltip(r)"
                      (click)="toggle(r.index)">{{ iconChar(r) }}</button>
            }
          </div>
          <span class="run-timeline__aggregate" data-testid="runs-aggregate">
            {{ visibleRuns().length }} run{{ visibleRuns().length === 1 ? '' : 's' }}
            @if (statusSummary().completed > 0) { · {{ statusSummary().completed }} ok }
            @if (statusSummary().failed > 0)    { · {{ statusSummary().failed }} fail }
            @if (statusSummary().running > 0)   { · {{ statusSummary().running }} live }
            @if (statusSummary().other > 0)     { · {{ statusSummary().other }} other }
          </span>
          <button type="button"
                  class="run-timeline__listtoggle"
                  data-testid="runs-toggle"
                  [attr.aria-expanded]="listExpanded()"
                  [title]="listExpanded() ? 'Collapse run list' : 'Show full run list'"
                  (click)="toggleListExpanded()">{{ listExpanded() ? '▾' : '▸' }}</button>
        </div>

        <!--
          When a single run icon is clicked we render that one run's
          full card (the same content the legacy expanded list showed).
          This is the in-place popover variant of "click an icon for
          the full card" — the data the user is looking for is the
          first thing they see, no modal stack to navigate.
        -->
        @if (expandedIndex() != null) {
          @for (r of visibleRuns(); track r.index) {
            @if (r.index === expandedIndex()) {
              <div class="run-card run-card--popover"
                   [attr.data-status]="r.status"
                   [attr.data-testid]="'run-popover-' + r.index">
                <div class="run-card__head run-card__head--peek">
                  <span class="run-card__index">#{{ r.index }}</span>
                  <span class="run-card__chip run-card__chip--intent" [attr.data-intent]="r.intent">{{ intentLabel(r.intent) }}</span>
                  <span class="run-card__chip run-card__chip--status" [attr.data-status]="r.status">{{ statusLabel(r) }}</span>
                  <span class="run-card__followup" [class.run-card__followup--empty]="!r.userFollowup">
                    {{ r.userFollowup || '(no follow-up)' }}
                  </span>
                  <span class="run-card__meta">
                    @if (r.durationSeconds != null) { <span>{{ formatDuration(r.durationSeconds) }}</span> }
                    @if (r.exitCode != null) { <span class="run-card__exit">exit {{ r.exitCode }}</span> }
                    <span class="run-card__time">{{ formatTime(r.startedAt) }}</span>
                  </span>
                  <button type="button"
                          class="run-card__close"
                          [attr.data-testid]="'run-popover-close-' + r.index"
                          aria-label="Close run details"
                          title="Close"
                          (click)="closePopover(); $event.stopPropagation()">×</button>
                </div>
                <div class="run-card__body">
                  @if (r.reason) {
                    <div class="run-card__row"><span class="run-card__row-label">Reason</span><span>{{ r.reason }}</span></div>
                  }
                  @if (r.inputSessionId || r.capturedSessionId) {
                    <div class="run-card__row">
                      <span class="run-card__row-label">Session</span>
                      <span class="run-card__session">
                        @if (r.inputSessionId) { <code>{{ short(r.inputSessionId) }}</code> → }
                        @if (r.capturedSessionId) { <code>{{ short(r.capturedSessionId) }}</code> }
                        @if (!r.capturedSessionId) { <em>not captured</em> }
                      </span>
                    </div>
                  }
                  <div class="run-card__row run-card__row--commits">
                    <span class="run-card__row-label">Software change</span>
                    @if (commitsState() === 'loading') {
                      <span class="run-card__commits-loading">loading…</span>
                    } @else if (commitsError()) {
                      <span class="run-card__commits-error">{{ commitsError() }}</span>
                    } @else if (commits().length === 0) {
                      <span class="run-card__commits-empty">No commits in this run's window.</span>
                    } @else {
                      <div class="run-card__commits">
                        <div class="run-card__commits-summary">
                          {{ commits().length }} commit{{ commits().length === 1 ? '' : 's' }} ·
                          +{{ totalAdded() }} / -{{ totalRemoved() }} across {{ totalFiles() }} file{{ totalFiles() === 1 ? '' : 's' }}
                        </div>
                        <ul class="run-card__commit-list">
                          @for (c of commits(); track c.sha) {
                            <li class="run-card__commit">
                              <code class="run-card__commit-sha">{{ c.shortSha }}</code>
                              <span class="run-card__commit-subject">{{ c.subject }}</span>
                              <span class="run-card__commit-stats">+{{ c.added }}/-{{ c.removed }} · {{ c.filesChanged }}f</span>
                            </li>
                          }
                        </ul>
                      </div>
                    }
                  </div>
                  <div class="run-card__row run-card__row--actions">
                    <button type="button"
                            class="run-card__filter"
                            (click)="emitFilter(r); $event.stopPropagation()"
                            [disabled]="r.lineStart == null">
                      Filter activity log to this run
                    </button>
                    <button type="button"
                            class="run-card__filter run-card__filter--primary"
                            (click)="openGitViewer.emit(r); $event.stopPropagation()"
                            [disabled]="!r.headShaBefore || !r.headShaAfter || r.headShaBefore === r.headShaAfter"
                            [attr.title]="(!r.headShaBefore || !r.headShaAfter)
                              ? 'No HEAD SHAs captured for this run (older run or repo unavailable).'
                              : (r.headShaBefore === r.headShaAfter
                                ? 'No commits made during this run.'
                                : 'Open the file-tree + diff viewer for this run.')">
                      Open git viewer
                    </button>
                  </div>
                </div>
              </div>
            }
          }
        }

        @if (listExpanded()) {
        @for (r of visibleRuns(); track r.index) {
          <div class="run-card"
               [attr.data-status]="r.status"
               [class.run-card--expanded]="expandedIndex() === r.index"
               [attr.data-testid]="'run-card-' + r.index">
            <button type="button"
                    class="run-card__head"
                    (click)="toggle(r.index)"
                    [attr.aria-expanded]="expandedIndex() === r.index">
              <span class="run-card__index">#{{ r.index }}</span>
              <span class="run-card__chip run-card__chip--intent" [attr.data-intent]="r.intent">{{ intentLabel(r.intent) }}</span>
              <span class="run-card__chip run-card__chip--status" [attr.data-status]="r.status">{{ statusLabel(r) }}</span>
              <span class="run-card__followup" [class.run-card__followup--empty]="!r.userFollowup">
                {{ r.userFollowup || '(no follow-up)' }}
              </span>
              <span class="run-card__meta">
                @if (r.durationSeconds != null) {
                  <span>{{ formatDuration(r.durationSeconds) }}</span>
                }
                @if (r.exitCode != null) {
                  <span class="run-card__exit">exit {{ r.exitCode }}</span>
                }
                <span class="run-card__time">{{ formatTime(r.startedAt) }}</span>
              </span>
              <span class="run-card__caret">{{ expandedIndex() === r.index ? '▾' : '▸' }}</span>
            </button>
            @if (expandedIndex() === r.index) {
              <div class="run-card__body">
                @if (r.reason) {
                  <div class="run-card__row"><span class="run-card__row-label">Reason</span><span>{{ r.reason }}</span></div>
                }
                @if (r.inputSessionId || r.capturedSessionId) {
                  <div class="run-card__row">
                    <span class="run-card__row-label">Session</span>
                    <span class="run-card__session">
                      @if (r.inputSessionId) { <code>{{ short(r.inputSessionId) }}</code> → }
                      @if (r.capturedSessionId) { <code>{{ short(r.capturedSessionId) }}</code> }
                      @if (!r.capturedSessionId) { <em>not captured</em> }
                    </span>
                  </div>
                }
                <div class="run-card__row run-card__row--commits">
                  <span class="run-card__row-label">Software change</span>
                  @if (commitsState() === 'loading') {
                    <span class="run-card__commits-loading">loading…</span>
                  } @else if (commitsError()) {
                    <span class="run-card__commits-error">{{ commitsError() }}</span>
                  } @else if (commits().length === 0) {
                    <span class="run-card__commits-empty">No commits in this run's window.</span>
                  } @else {
                    <div class="run-card__commits">
                      <div class="run-card__commits-summary">
                        {{ commits().length }} commit{{ commits().length === 1 ? '' : 's' }} ·
                        +{{ totalAdded() }} / -{{ totalRemoved() }} across {{ totalFiles() }} file{{ totalFiles() === 1 ? '' : 's' }}
                      </div>
                      <ul class="run-card__commit-list">
                        @for (c of commits(); track c.sha) {
                          <li class="run-card__commit">
                            <code class="run-card__commit-sha">{{ c.shortSha }}</code>
                            <span class="run-card__commit-subject">{{ c.subject }}</span>
                            <span class="run-card__commit-stats">+{{ c.added }}/-{{ c.removed }} · {{ c.filesChanged }}f</span>
                          </li>
                        }
                      </ul>
                    </div>
                  }
                </div>
                <div class="run-card__row run-card__row--actions">
                  <button type="button"
                          class="run-card__filter"
                          (click)="emitFilter(r); $event.stopPropagation()"
                          [disabled]="r.lineStart == null">
                    Filter activity log to this run
                  </button>
                  <button type="button"
                          class="run-card__filter run-card__filter--primary"
                          (click)="openGitViewer.emit(r); $event.stopPropagation()"
                          [disabled]="!r.headShaBefore || !r.headShaAfter || r.headShaBefore === r.headShaAfter"
                          [attr.title]="(!r.headShaBefore || !r.headShaAfter)
                            ? 'No HEAD SHAs captured for this run (older run or repo unavailable).'
                            : (r.headShaBefore === r.headShaAfter
                              ? 'No commits made during this run.'
                              : 'Open the file-tree + diff viewer for this run.')">
                    Open git viewer
                  </button>
                </div>
              </div>
            }
          </div>
        }
        }
      </div>
    }
  `,
  styles: [`
    .run-timeline { display: flex; flex-direction: column; gap: 4px; padding: 4px 0 8px; }
    .run-timeline__empty { padding: 8px 12px; font-size: 12.5px; color: #94a3b8; font-style: italic; }
    .run-timeline__header { display: flex; align-items: center; gap: 8px; padding: 4px 6px; flex-wrap: wrap; }
    .run-timeline__header--toggle {
      width: 100%; background: transparent; border: 1px solid rgba(148, 163, 184, 0.18);
      border-radius: 6px; cursor: pointer; color: inherit; font: inherit; text-align: left;
    }
    .run-timeline__header--toggle:hover { background: rgba(148, 163, 184, 0.08); }

    /*
     * Icon row (chat-first redesign): one square per run, status-tinted,
     * tooltip carries full information so the icon stays calm. Click
     * expands the run inline below.
     */
    .run-timeline__header--icons {
      flex-wrap: nowrap;
      gap: 6px;
      padding: 2px 4px;
      border: 1px solid rgba(148, 163, 184, 0.16);
      border-radius: 3px;
      background: rgba(30, 41, 59, 0.30);
      min-height: 26px;
    }
    .run-timeline__icons {
      display: inline-flex;
      align-items: center;
      gap: 3px;
      flex: 0 1 auto;
      overflow: hidden;
      flex-wrap: wrap;
      max-height: 22px;
    }
    .run-timeline__icon {
      width: 18px;
      height: 18px;
      padding: 0;
      display: inline-grid;
      place-items: center;
      border-radius: 2px;
      border: 1px solid rgba(148, 163, 184, 0.30);
      background: rgba(148, 163, 184, 0.18);
      color: #e2e8f0;
      font-size: 11px;
      line-height: 1;
      cursor: pointer;
      font-variant-numeric: tabular-nums;
    }
    .run-timeline__icon:hover { filter: brightness(1.15); }
    .run-timeline__icon[data-status="completed"] { background: rgba(34, 197, 94, 0.28); border-color: rgba(74, 222, 128, 0.55); color: #bbf7d0; }
    .run-timeline__icon[data-status="failed"]    { background: rgba(220, 38, 38, 0.40); border-color: rgba(248, 113, 113, 0.55); color: #fecaca; }
    .run-timeline__icon[data-status="running"]   { background: rgba(56, 189, 248, 0.30); border-color: rgba(125, 211, 252, 0.60); color: #bae6fd; }
    .run-timeline__icon[data-status="stopped"],
    .run-timeline__icon[data-status="cancelled"] { background: rgba(251, 191, 36, 0.26); border-color: rgba(251, 191, 36, 0.50); color: #fde68a; }
    .run-timeline__icon--active { box-shadow: 0 0 0 2px rgba(196, 181, 253, 0.55); }
    .run-timeline__listtoggle {
      width: 22px; height: 22px;
      padding: 0; display: inline-grid; place-items: center;
      border-radius: 2px; border: 1px solid rgba(148, 163, 184, 0.30);
      background: transparent; color: #94a3b8; cursor: pointer; font-size: 12px;
    }
    .run-timeline__listtoggle:hover { background: rgba(148, 163, 184, 0.10); color: #e2e8f0; }
    .run-card--popover { margin-top: 2px; }
    .run-card__close {
      margin-left: auto;
      width: 20px; height: 20px;
      padding: 0; display: inline-grid; place-items: center;
      border-radius: 2px; border: 1px solid rgba(148, 163, 184, 0.30);
      background: transparent; color: #94a3b8; cursor: pointer; font-size: 14px; line-height: 1;
    }
    .run-card__close:hover { background: rgba(148, 163, 184, 0.10); color: #f1f5f9; }
    .run-timeline__caret { width: 12px; text-align: center; color: #94a3b8; font-size: 11px; }
    .run-timeline__title { font-size: 11.5px; text-transform: uppercase; letter-spacing: 0.06em; color: #cbd5e1; font-weight: 600; }
    .run-timeline__aggregate { font-size: 11.5px; color: #94a3b8; }
    .run-timeline__chip { font-size: 10.5px; padding: 1px 6px; border-radius: 999px; background: rgba(148, 163, 184, 0.18); color: #e2e8f0; }
    .run-timeline__chip--ok   { background: rgba(34, 197, 94, 0.20); color: #bbf7d0; }
    .run-timeline__chip--bad  { background: rgba(220, 38, 38, 0.30); color: #fecaca; }
    .run-timeline__chip--live { background: rgba(56, 189, 248, 0.25); color: #bae6fd; }
    .run-timeline__chip--other{ background: rgba(251, 191, 36, 0.25); color: #fde68a; }
    .run-timeline__latest { margin-left: auto; font-size: 11px; color: #94a3b8; display: flex; gap: 6px; align-items: baseline; }
    .run-timeline__latest-status[data-status="failed"]   { color: #fca5a5; }
    .run-timeline__latest-status[data-status="completed"]{ color: #86efac; }
    .run-timeline__latest-status[data-status="running"]  { color: #bae6fd; }
    .run-timeline__latest-time { color: #64748b; }
    .run-card--peek { opacity: 0.95; }
    .run-card__head--peek { cursor: default; }

    .run-card { border: 1px solid rgba(148, 163, 184, 0.20); border-radius: 8px; background: rgba(30, 41, 59, 0.40); overflow: hidden; }
    .run-card[data-status="completed"] { border-color: rgba(74, 222, 128, 0.32); }
    .run-card[data-status="failed"]    { border-color: rgba(248, 113, 113, 0.45); background: rgba(220, 38, 38, 0.08); }
    .run-card[data-status="stopped"]   { border-color: rgba(251, 191, 36, 0.40); }
    .run-card[data-status="cancelled"] { border-color: rgba(251, 191, 36, 0.40); }
    .run-card[data-status="running"]   { border-color: rgba(125, 211, 252, 0.45); background: rgba(56, 189, 248, 0.08); }

    .run-card__head { display: flex; align-items: center; gap: 8px; width: 100%; background: transparent; border: 0; color: inherit; cursor: pointer; padding: 7px 10px; text-align: left; font: inherit; }
    .run-card__head:hover { background: rgba(148, 163, 184, 0.08); }
    .run-card__index { font-size: 11px; color: #94a3b8; min-width: 22px; }
    .run-card__chip { font-size: 10.5px; padding: 1px 6px; border-radius: 999px; background: rgba(148, 163, 184, 0.18); color: #e2e8f0; text-transform: lowercase; flex: 0 0 auto; }
    .run-card__chip--intent[data-intent="recovery"] { background: rgba(251, 191, 36, 0.30); color: #fde68a; }
    .run-card__chip--intent[data-intent="continue"] { background: rgba(125, 211, 252, 0.25); color: #bae6fd; }
    .run-card__chip--intent[data-intent="restart"]  { background: rgba(196, 181, 253, 0.30); color: #ede9fe; }
    .run-card__chip--status[data-status="completed"] { background: rgba(34, 197, 94, 0.28); color: #bbf7d0; }
    .run-card__chip--status[data-status="failed"]    { background: rgba(220, 38, 38, 0.40); color: #fecaca; }
    .run-card__chip--status[data-status="stopped"]   { background: rgba(251, 191, 36, 0.30); color: #fde68a; }
    .run-card__chip--status[data-status="cancelled"] { background: rgba(251, 191, 36, 0.30); color: #fde68a; }
    .run-card__chip--status[data-status="running"]   { background: rgba(56, 189, 248, 0.30); color: #bae6fd; }

    .run-card__followup { flex: 1 1 auto; font-size: 12.5px; color: #e2e8f0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; }
    .run-card__followup--empty { color: #64748b; font-style: italic; }
    .run-card__meta { font-size: 11px; color: #94a3b8; display: flex; gap: 8px; align-items: center; flex: 0 0 auto; }
    .run-card__exit { color: #fca5a5; }
    .run-card__caret { width: 14px; text-align: center; color: #94a3b8; }

    .run-card__body { padding: 8px 12px 10px; border-top: 1px solid rgba(148, 163, 184, 0.18); display: flex; flex-direction: column; gap: 6px; font-size: 12px; color: #e2e8f0; }
    .run-card__row { display: flex; gap: 10px; align-items: flex-start; }
    .run-card__row-label { width: 110px; flex: 0 0 110px; color: #94a3b8; font-size: 11.5px; }
    .run-card__session code { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 11px; }
    .run-card__commits-summary { color: #cbd5e1; margin-bottom: 4px; }
    .run-card__commit-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 2px; }
    .run-card__commit { display: flex; gap: 8px; align-items: baseline; }
    .run-card__commit-sha { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 11px; color: #94a3b8; flex: 0 0 auto; }
    .run-card__commit-subject { flex: 1 1 auto; }
    .run-card__commit-stats { color: #94a3b8; font-size: 11px; flex: 0 0 auto; }
    .run-card__commits-loading, .run-card__commits-empty, .run-card__commits-error { color: #94a3b8; font-style: italic; font-size: 11.5px; }
    .run-card__commits-error { color: #fca5a5; }

    .run-card__row--actions { gap: 6px; }
    .run-card__filter { background: transparent; border: 1px solid rgba(148, 163, 184, 0.30); color: #e2e8f0; padding: 3px 8px; border-radius: 6px; font-size: 11.5px; cursor: pointer; }
    .run-card__filter:hover:not(:disabled) { background: rgba(148, 163, 184, 0.12); }
    .run-card__filter:disabled { opacity: 0.5; cursor: not-allowed; }
    .run-card__filter--primary { border-color: rgba(125, 211, 252, 0.45); color: #e0f2fe; }
    .run-card__filter--primary:hover:not(:disabled) { background: rgba(125, 211, 252, 0.16); }
  `]
})
export class RunTimelineComponent {
  readonly job = input<JobInfo | null>(null);
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

  private readonly jobService = inject(JobService);

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
