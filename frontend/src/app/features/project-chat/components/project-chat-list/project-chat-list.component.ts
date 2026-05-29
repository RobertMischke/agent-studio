import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  OnInit,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';

import { FormsModule } from '@angular/forms';
import { TaskService } from '../../../../services/task.service';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';
import type { ProjectChatSearchHit, ProjectChatTurn } from '../../../../features/project-chat';
import { ProjectChatRailComponent } from '../project-chat-rail/project-chat-rail.component';
import { decideLoadAction, formatLoadedSummary } from './load-strategy';
import { TooltipDirective } from '../../../../components/tooltip';
import {
  ChatRowComponent,
  type ChatRowInput,
} from '../../../../components/chat-row/chat-row.component';
import {
  RoleBadgeComponent,
  PhaseSummaryListComponent,
  groupIntoPhases,
  type ChatPhase,
  type PhaseInputMessage,
} from '../../../workforce';

/**
 * Slice D virtualised chat list. Replaces the previous "render every
 * turn" rendering inside `orchestrator-side-sheet` with a windowed view
 * over the per-month markdown corpus served by
 * `/api/projects/{project}/chat/scroll`. Three modes:
 *
 * - **live**: paginate by ts cursor, append newest turns from the
 *   existing SignalR stream when they arrive (the parent component
 *   forwards them via `appendLive`).
 * - **search**: BM25-ranked FTS5 hits with `<b>...</b>` snippet markup
 *   resolved client-side to `<mark>` for accessibility + Catppuccin
 *   styling. Click a result → returns to live and scrolls + flashes
 *   that turn.
 *
 * Virtualisation is range-based: we render only the visible viewport
 * plus a 50-turn over-scroll buffer above and below. With a 120 px
 * default row, that's at most ~150 DOM nodes regardless of how many
 * thousand turns the project has.
 */
@Component({
  selector: 'app-project-chat-list',
  standalone: true,
  imports: [
    FormsModule,
    ProjectChatRailComponent,
    RoleBadgeComponent,
    PhaseSummaryListComponent,
    TooltipDirective,
    ChatRowComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-chat-list.component.html',
  styleUrl: './project-chat-list.component.scss',
})
export class ProjectChatListComponent implements OnInit, OnDestroy {
  readonly project = input<string | null>(null);

  /** Emits when the user clicks a search result so the host can reset
   *  any per-project state (e.g. local optimistic turns). */
  readonly turnSelected = output<{ turnId: string }>();

  @ViewChild('scrollHost', { static: true }) scrollHost!: ElementRef<HTMLDivElement>;

  // ── Live mode ─────────────────────────────────────────────────────
  readonly allTurns = signal<ProjectChatTurn[]>([]);
  readonly loadingInitial = signal(false);
  readonly loadingOlder = signal(false);
  readonly hasMoreOlder = signal(true);
  readonly errorMsg = signal<string | null>(null);
  private readonly seenIds = new Set<string>();

  // ── Search mode ───────────────────────────────────────────────────
  readonly mode = signal<'live' | 'search'>('live');
  readonly searchQuery = signal('');
  readonly searchHits = signal<ProjectChatSearchHit[]>([]);
  readonly searching = signal(false);
  private searchSubmittedQuery = '';

  // ── Virtualisation state ──────────────────────────────────────────
  readonly visibleStart = signal(0);
  readonly visibleEnd = signal(50);
  readonly rowHeightPx = 120; // estimate; tuned for typical short turns
  readonly bufferRows = 50;

  // ── Step-load panel state ─────────────────────────────────────────
  /**
   * Past this many loaded turns, the silent on-scroll backfill stops
   * and the step-load panel takes over. Operator decides how much
   * further to go — see prompt's "scroll for days/weeks must not freeze
   * the browser" requirement.
   */
  readonly deepHistoryThreshold = 1000;
  /** Confirm before doing a "jump to start" when total exceeds this. */
  readonly jumpToStartConfirmAt = 2000;
  readonly totalCount = signal<number | null>(null);
  readonly oldestServerTs = signal<string | null>(null);
  readonly jumpDate = signal('');

  // ── Phase summary layer ──────────────────────────────────────────
  /**
   * User-driven expansion overrides. The default expansion is "only the
   * newest phase is open", computed inside `expandedPhaseIds` when this
   * set is empty.
   */
  readonly phaseOverrides = signal<ReadonlyMap<string, boolean>>(new Map());

  readonly phases = computed<ChatPhase[]>(() => {
    const input: PhaseInputMessage[] = this.allTurns().map((t) => ({
      id: t.turnId,
      ts: t.ts,
      author: t.author,
      kind: t.kind,
      refs: t.refs ?? null,
    }));
    return groupIntoPhases(input);
  });

  readonly expandedPhaseIds = computed<ReadonlySet<string>>(() => {
    const phases = this.phases();
    const overrides = this.phaseOverrides();
    if (overrides.size === 0) {
      if (phases.length === 0) return new Set();
      return new Set([phases[phases.length - 1].id]);
    }
    // Default newest-expanded baseline, then apply overrides on top so a
    // user's explicit collapse of the newest phase is honoured.
    const baseline = new Set<string>();
    if (phases.length > 0) baseline.add(phases[phases.length - 1].id);
    for (const [id, expanded] of overrides) {
      if (expanded) baseline.add(id);
      else baseline.delete(id);
    }
    return baseline;
  });

  /** Turn ids that belong to a collapsed phase. Hidden from the timeline. */
  readonly hiddenTurnIds = computed<ReadonlySet<string>>(() => {
    const expanded = this.expandedPhaseIds();
    const hidden = new Set<string>();
    for (const phase of this.phases()) {
      if (expanded.has(phase.id)) continue;
      for (const id of phase.messageIds) hidden.add(id);
    }
    return hidden;
  });

  readonly flashTurnId = signal<string | null>(null);
  private flashTimer: ReturnType<typeof setTimeout> | null = null;

  /**
   * Turns that survive the phase-collapse filter. Drives the virtualised
   * window so collapsing an older phase removes its rows from the timeline
   * without disturbing the loaded chat-history substrate.
   */
  readonly visibleTurns = computed<ProjectChatTurn[]>(() => {
    const hidden = this.hiddenTurnIds();
    if (hidden.size === 0) return this.allTurns();
    return this.allTurns().filter((t) => !hidden.has(t.turnId));
  });

  readonly windowedTurns = computed<ProjectChatTurn[]>(() => {
    const all = this.visibleTurns();
    const start = Math.max(0, this.visibleStart());
    const end = Math.min(all.length, this.visibleEnd());
    return all.slice(start, end);
  });

  readonly topSpacerPx = computed(() => this.visibleStart() * this.rowHeightPx);
  readonly bottomSpacerPx = computed(() => {
    const remaining = this.visibleTurns().length - this.visibleEnd();
    return Math.max(0, remaining) * this.rowHeightPx;
  });

  /** Threshold reached + still more older history available + not searching. */
  readonly showStepLoadPanel = computed(() => {
    if (this.mode() !== 'live') return false;
    if (!this.hasMoreOlder()) return false;
    return this.allTurns().length >= this.deepHistoryThreshold;
  });

  /** Headline rendered inside the step-load panel. Pure-function call
   *  keeps the wording locked by `load-strategy.spec.ts`. */
  readonly stepLoadSummary = computed(() => {
    const all = this.allTurns();
    const oldest = all.length ? all[0].ts : this.oldestServerTs();
    return formatLoadedSummary(all.length, this.totalCount(), oldest);
  });

  private readonly jobService = inject(TaskService);
  private readonly confirmDialog = inject(ConfirmDialogService);
  private searchDebounceTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // Reload from scratch when the active project changes.
    effect(() => {
      const proj = this.project();
      if (!proj) {
        this.allTurns.set([]);
        this.seenIds.clear();
        return;
      }
      this.resetAndLoad();
    });
  }

  ngOnInit(): void {
    // Initial window calc deferred until host scroll element is sized.
    queueMicrotask(() => this.recomputeWindow());
  }

  ngOnDestroy(): void {
    if (this.flashTimer != null) clearTimeout(this.flashTimer);
    if (this.searchDebounceTimer != null) clearTimeout(this.searchDebounceTimer);
  }

  /**
   * Append a turn that arrived live (e.g. from the existing chat
   * write-path). The host knows whether the user is at the bottom and
   * can decide whether to also auto-scroll; we only update state.
   */
  appendLive(turn: ProjectChatTurn): void {
    if (this.seenIds.has(turn.turnId)) return;
    this.seenIds.add(turn.turnId);
    this.allTurns.update((curr) => {
      // Live appends are most-recent; the list is chronological.
      const next = [...curr, turn];
      next.sort((a, b) => a.ts.localeCompare(b.ts));
      return next;
    });
    this.recomputeWindow();
  }

  resetAndLoad(): void {
    this.allTurns.set([]);
    this.seenIds.clear();
    this.hasMoreOlder.set(true);
    this.loadingInitial.set(true);
    this.errorMsg.set(null);
    this.totalCount.set(null);
    this.oldestServerTs.set(null);
    const proj = this.project();
    if (!proj) {
      this.loadingInitial.set(false);
      return;
    }
    // Stats is best-effort: drives the panel headline. Failure should
    // not abort the chat load itself.
    this.jobService.getProjectChatStats(proj).subscribe({
      next: (resp) => {
        this.totalCount.set(resp.totalCount ?? null);
        this.oldestServerTs.set(resp.oldestTs ?? null);
      },
      error: () => {
        /* tolerate; panel falls back to loaded-only counts */
      },
    });
    this.jobService.scrollProjectChat(proj, { limit: 50 }).subscribe({
      next: (resp) => {
        // The /scroll tail returns reverse-chronological; flip so the
        // chat reads top-to-bottom oldest-to-newest like an IRC log.
        const ordered = [...(resp.turns ?? [])].reverse();
        for (const t of ordered) this.seenIds.add(t.turnId);
        this.allTurns.set(ordered);
        this.loadingInitial.set(false);
        this.hasMoreOlder.set(ordered.length === 50);
        // Snap to bottom on first load so the user sees recent turns.
        queueMicrotask(() => {
          this.scrollHost.nativeElement.scrollTop = this.scrollHost.nativeElement.scrollHeight;
          this.recomputeWindow();
        });
      },
      error: (err) => {
        this.errorMsg.set(err?.error?.error || err?.message || 'Failed to load chat');
        this.loadingInitial.set(false);
      },
    });
  }

  /**
   * Internal one-page backfill. Returns the number of fresh turns that
   * actually landed in the list (deduped by id) so the step-load loop
   * can decide whether to keep paging.
   */
  private loadOlder(pageSize = 50): Promise<number> {
    return new Promise((resolve) => {
      if (this.loadingOlder() || !this.hasMoreOlder()) {
        resolve(0);
        return;
      }
      const proj = this.project();
      if (!proj) {
        resolve(0);
        return;
      }
      const all = this.allTurns();
      if (all.length === 0) {
        resolve(0);
        return;
      }
      this.loadingOlder.set(true);
      const oldest = all[0].ts;
      const host = this.scrollHost.nativeElement;
      const beforeHeight = host.scrollHeight;
      const beforeTop = host.scrollTop;
      this.jobService.scrollProjectChat(proj, { before: oldest, limit: pageSize }).subscribe({
        next: (resp) => {
          const fetched = [...(resp.turns ?? [])].reverse(); // chronological
          const fresh = fetched.filter((t) => !this.seenIds.has(t.turnId));
          for (const t of fresh) this.seenIds.add(t.turnId);
          if (fresh.length === 0) this.hasMoreOlder.set(false);
          this.allTurns.update((curr) => [...fresh, ...curr]);
          this.loadingOlder.set(false);
          if (resp.turns && resp.turns.length < pageSize) this.hasMoreOlder.set(false);
          // Preserve scroll position relative to the previously-top item.
          queueMicrotask(() => {
            const afterHeight = host.scrollHeight;
            host.scrollTop = beforeTop + (afterHeight - beforeHeight);
            this.recomputeWindow();
            resolve(fresh.length);
          });
        },
        error: (err) => {
          this.errorMsg.set(err?.error?.error || err?.message || 'Failed to load older turns');
          this.loadingOlder.set(false);
          resolve(0);
        },
      });
    });
  }

  onScroll(): void {
    this.recomputeWindow();
    const host = this.scrollHost.nativeElement;
    const isNearTop = host.scrollTop < 200;
    const action = decideLoadAction({
      loadedCount: this.allTurns().length,
      hasMoreOlder: this.hasMoreOlder(),
      isLoading: this.loadingOlder(),
      isNearTop,
      threshold: this.deepHistoryThreshold,
    });
    if (action === 'continue-backfill') {
      void this.loadOlder();
    }
    // 'show-panel' is implicit via showStepLoadPanel() in the template;
    // there is nothing to do here. 'no-op' speaks for itself.
  }

  private recomputeWindow(): void {
    const host = this.scrollHost.nativeElement;
    if (!host) return;
    const top = host.scrollTop;
    const viewportH = host.clientHeight || 600;
    const startIdx = Math.max(0, Math.floor(top / this.rowHeightPx) - this.bufferRows);
    const endIdx = Math.ceil((top + viewportH) / this.rowHeightPx) + this.bufferRows;
    if (startIdx !== this.visibleStart()) this.visibleStart.set(startIdx);
    if (endIdx !== this.visibleEnd()) this.visibleEnd.set(endIdx);
  }

  // ── Search ────────────────────────────────────────────────────────
  onSearchInput(event: Event): void {
    const value = (event.target as HTMLInputElement | null)?.value ?? '';
    this.searchQuery.set(value);
    if (this.searchDebounceTimer != null) clearTimeout(this.searchDebounceTimer);
    if (value.trim().length === 0) {
      this.searchHits.set([]);
      this.mode.set('live');
      return;
    }
    this.mode.set('search');
    // Debounce 200ms so each keystroke does not hit the index.
    this.searchDebounceTimer = setTimeout(() => this.runSearch(), 200);
  }

  runSearch(): void {
    const q = this.searchQuery().trim();
    const proj = this.project();
    if (!q || !proj) return;
    this.searchSubmittedQuery = q;
    this.searching.set(true);
    this.mode.set('search');
    this.jobService.searchProjectChat(proj, q, 20).subscribe({
      next: (resp) => {
        // Ignore late responses for queries the user already moved past.
        if (this.searchSubmittedQuery !== q) return;
        this.searchHits.set(resp.results ?? []);
        this.searching.set(false);
      },
      error: (err) => {
        this.searching.set(false);
        this.errorMsg.set(err?.error?.error || err?.message || 'Search failed');
      },
    });
  }

  exitSearch(): void {
    this.mode.set('live');
    this.searchQuery.set('');
    this.searchHits.set([]);
    queueMicrotask(() => this.recomputeWindow());
  }

  openHit(hit: ProjectChatSearchHit): void {
    this.exitSearch();
    this.scrollToTurn(hit.turnId);
    this.turnSelected.emit({ turnId: hit.turnId });
  }

  /** Slice C: rail chip click. The rail emits the source turnId; we
   *  reuse `scrollToTurn` so the same flash + virtualisation-anchored
   *  load path that the search-result click uses also drives the rail. */
  onRailChipSelect(event: { turnId: string }): void {
    this.scrollToTurn(event.turnId);
  }

  /** Phase-summary toggle. Records the user's explicit preference for
   *  this phase so the default "newest expanded, rest collapsed" can be
   *  overridden in either direction. */
  onPhaseToggled(event: { phaseId: string; expanded: boolean }): void {
    const next = new Map(this.phaseOverrides());
    next.set(event.phaseId, event.expanded);
    this.phaseOverrides.set(next);
    queueMicrotask(() => this.recomputeWindow());

    // User feedback: "in place expand" — clicking a phase summary row
    // must put the user's eye on the messages that just appeared, not
    // leave them looking at the index. When expanding, scroll the
    // verbatim chat to the phase's first turn so the unhide reads as
    // an inline reveal at the click site (the phase summary stays
    // pinned at the top while the chat below now shows the phase's
    // messages with a flash highlight on the first one).
    if (!event.expanded) return;
    const phase = this.phases().find((p) => p.id === event.phaseId);
    if (!phase || phase.messageIds.length === 0) return;
    const firstTurn = phase.messageIds[0];
    // Two ticks: one for the window recompute above, one for the DOM
    // to settle around the unhid turns before scrollToTurn measures.
    queueMicrotask(() => queueMicrotask(() => this.scrollToTurn(firstTurn)));
  }

  scrollToTurn(turnId: string): void {
    const proj = this.project();
    if (!proj) return;
    const all = this.allTurns();
    const known = all.find((t) => t.turnId === turnId);
    if (known) {
      this.flash(turnId);
      queueMicrotask(() => {
        const el = this.scrollHost.nativeElement.querySelector<HTMLElement>(
          `[data-turnid="${CSS.escape(turnId)}"]`,
        );
        el?.scrollIntoView({ behavior: 'smooth', block: 'center' });
      });
      return;
    }
    // Not in the loaded window: fetch a slice anchored at this turn's ts.
    this.jobService.getProjectChatTurn(proj, turnId).subscribe({
      next: (resp) => {
        const ts = resp.turn.ts;
        // Load 50 turns around the anchor by asking for `before=<ts+1>`.
        const after = new Date(new Date(ts).getTime() + 1).toISOString();
        this.jobService.scrollProjectChat(proj, { before: after, limit: 50 }).subscribe({
          next: (page) => {
            const ordered = [...(page.turns ?? [])].reverse();
            for (const t of ordered) this.seenIds.add(t.turnId);
            this.allTurns.set(ordered);
            this.hasMoreOlder.set(true);
            queueMicrotask(() => {
              const el = this.scrollHost.nativeElement.querySelector<HTMLElement>(
                `[data-turnid="${CSS.escape(turnId)}"]`,
              );
              el?.scrollIntoView({ behavior: 'auto', block: 'center' });
              this.recomputeWindow();
              this.flash(turnId);
            });
          },
        });
      },
    });
  }

  private flash(turnId: string): void {
    this.flashTurnId.set(turnId);
    if (this.flashTimer != null) clearTimeout(this.flashTimer);
    this.flashTimer = setTimeout(() => this.flashTurnId.set(null), 1500);
  }

  /** Adapter from the project-chat turn shape to the shared row shape. */
  toChatRow(turn: ProjectChatTurn): ChatRowInput {
    return {
      id: turn.turnId,
      author: turn.author,
      kind: turn.kind,
      refs: turn.refs ?? null,
      ts: turn.ts,
      body: turn.body,
      userVariant: turn.author === 'user',
      eventVariant: turn.kind !== 'turn',
      flash: turn.turnId === this.flashTurnId(),
    };
  }

  renderSnippet(snippet: string): string {
    // Backend returns HTML-encoded text with `<b>...</b>` markers
    // preserved. Map to <mark> for accessibility and keep the rest
    // as-is so the host bodies cannot inject arbitrary HTML.
    return (snippet || '').replace(/<b>/g, '<mark>').replace(/<\/b>/g, '</mark>');
  }

  // ── Step-load actions ─────────────────────────────────────────────
  /**
   * Page repeatedly until the oldest loaded turn predates `targetTs`,
   * or the server says there is no more older history, or we hit the
   * count safety cap. Each page is `loadOlder(200)`; scroll-position
   * preservation is handled by `loadOlder` itself.
   */
  async loadBackTo(targetTs: string, safetyCap = 5000): Promise<void> {
    const targetMs = new Date(targetTs).getTime();
    if (!Number.isFinite(targetMs)) return;
    let safety = 0;
    while (this.hasMoreOlder()) {
      const all = this.allTurns();
      const oldestLoaded = all.length ? new Date(all[0].ts).getTime() : Number.POSITIVE_INFINITY;
      if (oldestLoaded <= targetMs) break;
      if (safety++ > Math.ceil(safetyCap / 200)) break;
      const fresh = await this.loadOlder(200);
      if (fresh === 0) break;
    }
  }

  /** Page repeatedly until we have at least `targetCount` more turns or
   *  the server reports end-of-history. */
  async loadMoreMessages(targetExtra: number): Promise<void> {
    const startCount = this.allTurns().length;
    let safety = 0;
    while (this.hasMoreOlder()) {
      if (this.allTurns().length - startCount >= targetExtra) break;
      if (safety++ > Math.ceil(targetExtra / 200) + 1) break;
      const remaining = targetExtra - (this.allTurns().length - startCount);
      const page = Math.max(50, Math.min(200, remaining));
      const fresh = await this.loadOlder(page);
      if (fresh === 0) break;
    }
  }

  /** "Another day / week / month" — shifts the target backwards from
   *  the currently-oldest loaded turn by the given delta. */
  stepBackByDays(days: number): void {
    const all = this.allTurns();
    if (all.length === 0) return;
    const oldestMs = new Date(all[0].ts).getTime();
    if (!Number.isFinite(oldestMs)) return;
    const target = new Date(oldestMs - days * 24 * 3600 * 1000).toISOString();
    void this.loadBackTo(target);
  }

  /** "+N messages" step. */
  stepBackByCount(count: number): void {
    void this.loadMoreMessages(count);
  }

  /** "Jump to date…" — load every turn from the chosen day onward. */
  jumpToDate(): void {
    const raw = this.jumpDate();
    if (!raw) return;
    // <input type="date"> gives "YYYY-MM-DD"; treat as start-of-day UTC.
    const target = new Date(raw + 'T00:00:00Z').toISOString();
    void this.loadBackTo(target);
  }

  /** "Jump to start" — irreversibly load everything. Confirmed when
   *  total exceeds the soft threshold so a misclick on a giant chat
   *  cannot freeze the UI. */
  async jumpToStart(): Promise<void> {
    const total = this.totalCount();
    if (total != null && total > this.jumpToStartConfirmAt) {
      const ok = await this.confirmDialog.confirm({
        title: 'Load entire chat history?',
        message: `Load all ${total.toLocaleString('en-US')} messages? This may take a moment.`,
        confirmLabel: 'Load all',
        cancelLabel: 'Cancel',
        kind: 'primary',
      });
      if (!ok) return;
    }
    void this.loadMoreMessages(Number.MAX_SAFE_INTEGER);
  }

  onJumpDateInput(event: Event): void {
    const v = (event.target as HTMLInputElement | null)?.value ?? '';
    this.jumpDate.set(v);
  }

  formatTs(iso: string): string {
    try {
      return new Date(iso).toLocaleString(undefined, {
        month: 'short',
        day: '2-digit',
        hour: '2-digit',
        minute: '2-digit',
      });
    } catch {
      return iso;
    }
  }
}
