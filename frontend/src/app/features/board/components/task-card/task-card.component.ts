import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, OnInit, computed, effect, inject, input, output, signal } from '@angular/core';
import type { AutoLoopSnapshot, TaskInfo, PendingIntent, EpicRollup } from '../../../../models/task.model';
import { GitSummaryService } from '../../../../services/git-summary.service';
import { TaskService } from '../../../../services/task.service';
import { ClientService } from '../../../../services/client.service';
import { AutoReviewStatusStore } from '../../../../services/auto-review-status.store';
import { CodeReviewActivityStore } from '../../../../services/code-review-activity.store';
import { cliTypeIcon } from '../../../../services/format.util';
import { projectIdentity } from '../../../../services/project-identity.util';
import { TagRegistryStore } from '../../../../services/tag-registry.store';
import {
  buildAutoReviewProcessBadge,
  buildCardCtxMenuItems,
  buildCommitChainTooltip,
  buildCommitChainView,
  buildCommitEmptyBadge,
  buildEffectiveModelChip,
  buildExecutionBadge,
  buildHumanReviewBadge,
  buildLoopTooltip,
  buildModeBadge,
  buildOutcomeIssueBadge,
  buildOwnerChip,
  buildPendingTooltip,
  buildPhaseBadge,
  buildReviewBadge,
  buildTagChips,
  buildTaskTypeChip,
  buildTokenBubble,
  cardNeedsAttention,
  commitChainVariant,
  formatStateLabel,
  formatTokens,
  EPIC_ASSIGN_PREFIX,
  EPIC_DETACH_ID,
  FILTER_DEPENDENTS_ID,
  type CommitChainView,
  type CommitEmptyBadge,
} from './task-card-view-model';

import { TooltipDirective } from '../../../../components/tooltip';
import { TaskStatusPopoverDirective } from '../../../../components/task-status-card';
import { MenuComponent, MenuItemClickEvent } from '../../../../components/menu';
import { TokenPopoverDirective } from './token-popover.directive';
import { NotificationService } from '../../../../services/notification.service';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import { BoardFiltersService } from '../../state/board-filters.service';
// Shared 'now' signal that ticks every 30s so all relative timestamps update in lockstep
// without re-reading Date.now() during change detection (which causes NG0100).
const nowTick = signal(Date.now());
if (typeof window !== 'undefined') {
  setInterval(() => nowTick.set(Date.now()), 30_000);
}

@Component({
  selector: 'app-task-card, app-job-card',
  standalone: true,
  imports: [TooltipDirective, TaskStatusPopoverDirective, MenuComponent, TokenPopoverDirective],
  // OnPush + signal-based reactivity. With ~30+ cards in a single
  // 4-auto-review lane, default Zone CD on every microtask was cumulating
  // into 80-100 ms long tasks during scroll/poll bursts. The component's
  // template only reads signal inputs, computed signals, and the shared
  // `nowTick` signal, so OnPush updates remain correct without any
  // explicit `markForCheck` calls.
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-card.component.html',
  styleUrl: './task-card.component.scss',
})
export class TaskCardComponent implements OnInit, OnDestroy {
  readonly job = input.required<TaskInfo>();
  readonly compact = input<boolean>(false);
  /**
   * F2: when set and matches this card's job id, the card renders the
   * "just created" pulse highlight and scrolls itself into view on the
   * board. The host clears the signal after one animation cycle.
   */
  readonly highlightJobId = input<string | null>(null);
  readonly deleteRequested = output<TaskInfo>();
  /**
   * F5: emitted when the user clicks the inline "Pick next" affordance
   * on a 2-ready card. The host wires this to `moveJobToTop` so the
   * runner picks it up on the next cycle.
   */
  readonly pickNextRequested = output<TaskInfo>();
  private readonly hostRef = inject(ElementRef<HTMLElement>);

  /** True when this card should render the just-created highlight. */
  readonly isJustCreated = computed(() => this.highlightJobId() === this.job().id);

  /**
   * Scroll-into-view effect: when this card becomes the highlighted
   * "just created" target, scroll it into the board viewport so the
   * operator's eye lands on it even on a 200+ card board.
   */
  private readonly scrollEffect = effect(() => {
    if (!this.isJustCreated()) return;
    queueMicrotask(() => {
      try {
        this.hostRef.nativeElement.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' });
      } catch { /* SSR / detached DOM — ignore */ }
    });
  });
  private readonly gitSummary = inject(GitSummaryService);
  private readonly clients = inject(ClientService);
  private readonly tagRegistry = inject(TagRegistryStore);
  private readonly autoReviewStatus = inject(AutoReviewStatusStore);
  private readonly codeReviewActivity = inject(CodeReviewActivityStore);
  private stopPolling: (() => void) | null = null;

  readonly taskTypeChip = computed(() => buildTaskTypeChip(this.job().taskType));

  /**
   * Mode badge (planning / research). Null for coding so the board stays quiet
   * for the default mode; planning and research cards become recognizable at a
   * glance. See {@link buildModeBadge}.
   */
  readonly modeBadge = computed(() => buildModeBadge(this.job().mode));

  readonly tagChips = computed(() => buildTagChips(this.job().tags, this.tagRegistry.byId()));

  onDeleteClick(event: Event) {
    event.stopPropagation();
    this.deleteRequested.emit(this.job());
  }

  /** True for cards where "Pick next" makes sense (front-of-queue promotion). */
  readonly canPickNext = computed(() => this.job().state === '2-ready');

  onPickNextClick(event: Event) {
    event.stopPropagation();
    this.pickNextRequested.emit(this.job());
  }

  readonly ownerChip = computed(() => {
    const ownerId = this.job().ownerClientId;
    if (!ownerId) return null;
    return buildOwnerChip(this.clients.resolve(ownerId));
  });

  // Live working-tree state (branch + uncommitted file count) only makes
  // sense while the agent is actively touching the repo. In review lanes
  // the task is "frozen" against a specific commit and live state would
  // misrepresent it (the board's project status is shared across cards;
  // a card sitting in 5-human-review must not advertise the dev branch
  // someone else just switched to). Pre-work and post-review lanes carry
  // no useful per-task git context either, so the pill is suppressed
  // everywhere except 3-progress.
  private static readonly LANES_WITH_GIT = new Set([
    '3-progress',
  ]);

  readonly gitPill = computed(() => {
    if (!TaskCardComponent.LANES_WITH_GIT.has(this.job().state)) return null;
    const projectName = this.job().projectName;
    const summary = this.gitSummary.value().find(s => s.projectName === projectName);
    return summary && summary.isRepo ? summary : null;
  });

  /**
   * Commit-chain view model (AC#1/#4). Reads the attributed `commits[]`
   * chain (single source of truth), falling back to the legacy singular
   * `commit` only when `commits[]` is absent. Never sources commit data
   * from repo HEAD / the working tree - that was bug (1) ("main: 20 files"
   * leaking into review lanes).
   */
  readonly commitChainView = computed<CommitChainView | null>(() => {
    const variant = commitChainVariant(this.job().state);
    if (!variant) return null;
    return buildCommitChainView(this.job(), variant);
  });

  /**
   * Zero-commit diagnostic for review-lane cards (AC#3, bug (3)). See
   * {@link buildCommitEmptyBadge}.
   */
  readonly commitEmptyBadge = computed<CommitEmptyBadge | null>(() => buildCommitEmptyBadge(this.job()));

  readonly gitTooltip = computed(() => {
    const g = this.gitPill();
    if (!g) return '';
    return `Branch: ${g.branch ?? '(detached)'}\n${g.filesChanged} changed file(s) in ${g.rootPath}\n+${g.totalAdded} / −${g.totalRemoved}`;
  });

  /**
   * Commit-chain tooltip. A single commit lists the files it touched; a
   * multi-commit chain lists every SHA with its subject and rolled-up file
   * total so a card carrying auto-review concerns makes the affected scope
   * visible without opening the job. HTML escaping is handled by the tooltip
   * controller's DOMPurify pass.
   */
  readonly commitTooltip = computed(() => buildCommitChainTooltip(this.job()));

  ngOnInit(): void { this.stopPolling = this.gitSummary.ensurePolling(); }
  ngOnDestroy(): void { this.stopPolling?.(); }

  stateLabel(): string { return formatStateLabel(this.job().state); }

  phaseBadge() { return buildPhaseBadge(this.job().phase); }

  executionBadge() { return buildExecutionBadge(this.job()); }

  readonly reviewBadge = computed(() => buildReviewBadge(this.job().summaryState));

  readonly autoReviewProcessBadge = computed(() =>
    buildAutoReviewProcessBadge(this.job(), this.autoReviewStatus.status(), Date.now()),
  );

  readonly humanReviewBadge = computed(() => buildHumanReviewBadge(this.job()));

  /**
   * Host-level "this card needs a human" flag. Drives the red left ribbon +
   * faint tint that visually separates an escalated / reissue card from the
   * Completed/Archive cards it shares the "Done & Decide" column with.
   */
  readonly needsAttention = computed(() => cardNeedsAttention(this.job()));

  readonly outcomeIssueBadge = computed(() => buildOutcomeIssueBadge(this.job().outcomeIssue));

  /**
   * Card-level "code review running" flag. Reads the shared
   * {@link CodeReviewActivityStore} singleton the detail-pane panel marks
   * while a user-triggered review is in flight, so the operator sees the
   * pass progressing on the board even after navigating away from the task
   * (the user's "Progress an die Karte" requirement). Ephemeral: clears when
   * the synchronous review call resolves.
   */
  readonly codeReviewRunning = computed(() => {
    const job = this.job();
    return this.codeReviewActivity.isRunning(
      CodeReviewActivityStore.key(job.watchPath, job.id),
    );
  });

  /** Hot-state threshold: amber pill once the loop is at 80% of the iteration cap. */
  readonly loopHot = computed(() => {
    const al = this.job().autoLoop;
    if (!al || al.maxIterations <= 0) return false;
    return al.iteration / al.maxIterations >= 0.8;
  });

  loopTooltip(al: AutoLoopSnapshot): string { return buildLoopTooltip(al); }

  pendingTooltip(pi: PendingIntent): string { return buildPendingTooltip(pi); }

  /** Compact tokens label: 850 -> "850", 2400 -> "2.4k", 850000 -> "850k", 3_100_000 -> "3.1M". */
  formatTokens(n: number): string { return formatTokens(n); }

  /**
   * Token-bubble descriptor: returns null when the task has no recorded
   * orchestrator activity (input + output + cacheRead + cacheWrite == 0).
   */
  readonly tokenBubble = computed(() => buildTokenBubble(this.job().tokenSummary));

  readonly agentIcon = computed(() => {
    const t = this.job().cliType;
    return t ? cliTypeIcon(t) : '🤖';
  });

  readonly effectiveModelChip = computed(() =>
    buildEffectiveModelChip(this.job(), this.clients.resolve(this.job().ownerClientId))
  );

  readonly identity = computed(() => projectIdentity(this.job().projectName));

  /** Epic container card: drives the "EPIC" badge in the title row. */
  readonly isEpic = computed(() => this.job().kind === 'epic');

  /** Parent epic id when this card is a sub-task, else null (drives the "↳ epic" chip). */
  readonly subTaskEpicId = computed(() => {
    const id = this.job().epicId;
    return id && id.trim().length > 0 ? id : null;
  });

  readonly isRunning = computed(() =>
    this.job().state === '3-progress' && this.job().execution?.status === 'running'
  );

  /**
   * F34: dependsOn targets that are known and not yet complete. Drives the
   * card's `waiting on KEY` badge. Cards with no dependsOn edges short-circuit
   * before reading the board snapshot, so they never depend on `jobs()` and
   * the O(N) state lookup is paid only by the few cards that have dependencies.
   * Targets absent from the current board view are skipped (no false positive),
   * and completed/archived targets are satisfied.
   */
  readonly waitingOn = computed<string[]>(() => {
    const deps = this.job().references?.dependsOn ?? [];
    if (deps.length === 0) return [];
    const stateByKey = new Map<string, string>();
    for (const t of this.jobs.jobs()) {
      const k = (t.key ?? '').trim();
      if (k) stateByKey.set(k.toUpperCase(), t.state);
    }
    return deps.filter((dep) => {
      const st = stateByKey.get(dep.trim().toUpperCase());
      if (st === undefined) return false;
      return st !== '6-completed' && st !== '7-archive';
    });
  });

  /** Compact badge label: first waiting key, with a "+N" suffix for the rest. */
  readonly waitingOnLabel = computed<string | null>(() => {
    const waiting = this.waitingOn();
    if (waiting.length === 0) return null;
    return waiting.length === 1 ? waiting[0] : `${waiting[0]} +${waiting.length - 1}`;
  });

  readonly waitingOnTooltip = computed(() => {
    const waiting = this.waitingOn();
    if (waiting.length === 0) return '';
    return `Waiting on ${waiting.join(', ')} to complete before this task is workable.`;
  });

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

  // Context menu: copy actions + epic assignment (way 2).
  private readonly notifications = inject(NotificationService);
  private readonly jobs = inject(TaskService);
  private readonly boardFilters = inject(BoardFiltersService);
  readonly cardContextMenu = signal<{ x: number; y: number } | null>(null);
  /** Epics in this card's project, loaded on right-click for the assign submenu. */
  private readonly epicsForMenu = signal<EpicRollup[]>([]);

  readonly cardCtxMenuItems = computed(() =>
    buildCardCtxMenuItems(this.job(), this.isEpic(), this.epicsForMenu(), this.subTaskEpicId()),
  );

  openCardContextMenu(event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.cardContextMenu.set({ x: event.clientX, y: event.clientY });
    // Refresh the assign list each open (only for task cards). Best-effort:
    // the section just shows "No epics" on failure.
    if (!this.isEpic()) {
      const watchPath = this.job().watchPath;
      this.jobs.getEpics().subscribe({
        next: (list) => this.epicsForMenu.set((list ?? []).filter((e) => e.watchPath === watchPath)),
        error: () => this.epicsForMenu.set([]),
      });
    }
  }

  closeCardContextMenu(): void {
    this.cardContextMenu.set(null);
  }

  onCardCtxMenuItemClick(ev: MenuItemClickEvent): void {
    const job = this.job();

    if (ev.id.startsWith(EPIC_ASSIGN_PREFIX)) {
      const epicId = ev.id.slice(EPIC_ASSIGN_PREFIX.length);
      if (epicId === this.subTaskEpicId()) return; // already in this epic
      this.assignEpic(epicId);
      return;
    }
    if (ev.id === EPIC_DETACH_ID) {
      this.assignEpic(null);
      return;
    }
    if (ev.id === FILTER_DEPENDENTS_ID && job.key) {
      this.boardFilters.setDependsOnFilter(job.key);
      this.notifications.info(`Filtering to tasks that depend on ${job.key}`);
      return;
    }

    let text = '';
    let label = '';
    if (ev.id === 'copy-name') { text = job.title || job.id; label = 'Name'; }
    else if (ev.id === 'copy-id') { text = job.id; label = 'ID'; }
    else if (ev.id === 'copy-key' && job.key) { text = job.key; label = 'Key'; }
    if (text) {
      copyTextToClipboard(text).then(ok => {
        if (ok) this.notifications.success(`${label} copied`);
      });
    }
  }

  /** Way 2: attach (epicId) or detach (null) this task, then refresh the board. */
  private assignEpic(epicId: string | null): void {
    const job = this.job();
    this.jobs.setJobEpic(job.id, epicId, job.watchPath).subscribe({
      next: () => {
        const epic = this.epicsForMenu().find((e) => e.id === epicId);
        this.notifications.success(
          epicId ? `Assigned to epic: ${epic?.title ?? epicId}` : 'Detached from epic',
        );
        this.jobs.refresh(true);
      },
      error: () => this.notifications.error('Could not update epic assignment'),
    });
  }
}
