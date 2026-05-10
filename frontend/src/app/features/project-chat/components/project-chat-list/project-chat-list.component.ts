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
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { JobService } from '../../../../services/job.service';
import type { ProjectChatSearchHit, ProjectChatTurn } from '../../../../features/project-chat';
import { markdownToHtml } from '../../../../components/markdown-utils';
import { ProjectChatRailComponent } from '../project-chat-rail/project-chat-rail.component';

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
  imports: [CommonModule, FormsModule, ProjectChatRailComponent],
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

  readonly flashTurnId = signal<string | null>(null);
  private flashTimer: ReturnType<typeof setTimeout> | null = null;

  readonly windowedTurns = computed<ProjectChatTurn[]>(() => {
    const all = this.allTurns();
    const start = Math.max(0, this.visibleStart());
    const end = Math.min(all.length, this.visibleEnd());
    return all.slice(start, end);
  });

  readonly topSpacerPx = computed(() => this.visibleStart() * this.rowHeightPx);
  readonly bottomSpacerPx = computed(() => {
    const remaining = this.allTurns().length - this.visibleEnd();
    return Math.max(0, remaining) * this.rowHeightPx;
  });

  private readonly jobService = inject(JobService);
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
    const proj = this.project();
    if (!proj) {
      this.loadingInitial.set(false);
      return;
    }
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

  private loadOlder(): void {
    if (this.loadingOlder() || !this.hasMoreOlder()) return;
    const proj = this.project();
    if (!proj) return;
    const all = this.allTurns();
    if (all.length === 0) return;
    this.loadingOlder.set(true);
    const oldest = all[0].ts;
    const host = this.scrollHost.nativeElement;
    const beforeHeight = host.scrollHeight;
    const beforeTop = host.scrollTop;
    this.jobService.scrollProjectChat(proj, { before: oldest, limit: 50 }).subscribe({
      next: (resp) => {
        const fetched = [...(resp.turns ?? [])].reverse(); // chronological
        const fresh = fetched.filter((t) => !this.seenIds.has(t.turnId));
        for (const t of fresh) this.seenIds.add(t.turnId);
        if (fresh.length === 0) this.hasMoreOlder.set(false);
        this.allTurns.update((curr) => [...fresh, ...curr]);
        this.loadingOlder.set(false);
        if (resp.turns && resp.turns.length < 50) this.hasMoreOlder.set(false);
        // Preserve scroll position relative to the previously-top item.
        queueMicrotask(() => {
          const afterHeight = host.scrollHeight;
          host.scrollTop = beforeTop + (afterHeight - beforeHeight);
          this.recomputeWindow();
        });
      },
      error: (err) => {
        this.errorMsg.set(err?.error?.error || err?.message || 'Failed to load older turns');
        this.loadingOlder.set(false);
      },
    });
  }

  onScroll(): void {
    this.recomputeWindow();
    const host = this.scrollHost.nativeElement;
    if (host.scrollTop < 200) this.loadOlder();
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

  scrollToTurn(turnId: string): void {
    const proj = this.project();
    if (!proj) return;
    const all = this.allTurns();
    const known = all.find((t) => t.turnId === turnId);
    if (known) {
      this.flash(turnId);
      queueMicrotask(() => {
        const el = this.scrollHost.nativeElement.querySelector<HTMLElement>(
          `[data-turnid="${CSS.escape(turnId)}"]`
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
                `[data-turnid="${CSS.escape(turnId)}"]`
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

  // ── Rendering ─────────────────────────────────────────────────────
  renderBody(body: string): string {
    return markdownToHtml(body || '');
  }

  renderSnippet(snippet: string): string {
    // Backend returns HTML-encoded text with `<b>...</b>` markers
    // preserved. Map to <mark> for accessibility and keep the rest
    // as-is so the host bodies cannot inject arbitrary HTML.
    return (snippet || '')
      .replace(/<b>/g, '<mark>')
      .replace(/<\/b>/g, '</mark>');
  }

  formatTs(iso: string): string {
    try {
      return new Date(iso).toLocaleString(undefined, {
        month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit'
      });
    } catch {
      return iso;
    }
  }
}
