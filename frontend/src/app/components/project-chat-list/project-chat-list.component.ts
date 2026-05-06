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
import { JobService } from '../../services/job.service';
import {
  ProjectChatSearchHit,
  ProjectChatTurn,
} from '../../models/job.model';
import { markdownToHtml } from '../markdown-utils';

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
  imports: [CommonModule, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="pchat" data-testid="project-chat-list">
      <div class="pchat__search-bar" data-testid="pchat-search-bar">
        <span class="pchat__chip" [class.pchat__chip--active]="mode() === 'search'">
          {{ mode() === 'search' ? '🔍 search' : '💬 live' }}
        </span>
        <input
          #searchInput
          class="pchat__search-input"
          type="text"
          placeholder="Search chat history…"
          data-testid="pchat-search-input"
          [value]="searchQuery()"
          (input)="onSearchInput($event)"
          (keydown.enter)="runSearch()"
          (keydown.escape)="exitSearch()" />
        @if (mode() === 'search') {
          <button
            type="button"
            class="pchat__chip-btn"
            data-testid="pchat-back-to-live"
            (click)="exitSearch()">
            ← back to live
          </button>
        }
      </div>

      @if (errorMsg(); as e) {
        <div class="pchat__error" data-testid="pchat-error">{{ e }}</div>
      }

      <div
        #scrollHost
        class="pchat__scroll"
        data-testid="pchat-scroll"
        (scroll)="onScroll()">
        @if (mode() === 'live') {
          <div
            class="pchat__spacer pchat__spacer--top"
            [style.height.px]="topSpacerPx()"></div>

          @for (turn of windowedTurns(); track turn.turnId) {
            <article
              class="pchat__turn"
              [attr.data-testid]="'pchat-turn'"
              [attr.data-turnid]="turn.turnId"
              [class.pchat__turn--user]="turn.author === 'user'"
              [class.pchat__turn--event]="turn.kind !== 'turn'"
              [class.pchat__turn--flash]="turn.turnId === flashTurnId()">
              <header class="pchat__turn-head">
                <span class="pchat__author">{{ turn.author }}</span>
                <span class="pchat__kind">{{ turn.kind }}</span>
                <time class="pchat__ts" [attr.datetime]="turn.ts">{{ formatTs(turn.ts) }}</time>
              </header>
              <div class="pchat__body" [innerHTML]="renderBody(turn.body)"></div>
            </article>
          }

          <div
            class="pchat__spacer pchat__spacer--bottom"
            [style.height.px]="bottomSpacerPx()"></div>

          @if (loadingOlder()) {
            <div class="pchat__hint" data-testid="pchat-loading-older">Loading older turns…</div>
          }
          @if (allTurns().length === 0 && !loadingInitial()) {
            <div class="pchat__empty">No conversation yet.</div>
          }
        } @else {
          <div class="pchat__results" data-testid="pchat-search-results">
            @if (searching()) {
              <div class="pchat__hint">Searching…</div>
            }
            @for (hit of searchHits(); track hit.turnId) {
              <button
                type="button"
                class="pchat__hit"
                [attr.data-testid]="'pchat-hit'"
                [attr.data-turnid]="hit.turnId"
                (click)="openHit(hit)">
                <span class="pchat__hit-meta">
                  <span class="pchat__author">{{ hit.author }}</span>
                  <span class="pchat__kind">{{ hit.kind }}</span>
                  <time class="pchat__ts">{{ formatTs(hit.ts) }}</time>
                </span>
                <span class="pchat__hit-snippet" [innerHTML]="renderSnippet(hit.snippet)"></span>
              </button>
            }
            @if (!searching() && searchHits().length === 0 && searchQuery().trim()) {
              <div class="pchat__hint">No matches.</div>
            }
          </div>
        }
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; height: 100%; min-height: 0; }
    .pchat { display: flex; flex-direction: column; height: 100%; }
    .pchat__search-bar {
      display: flex;
      gap: 6px;
      align-items: center;
      padding: 6px 10px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
      background: rgba(255,255,255,0.02);
    }
    .pchat__chip {
      font-size: 11px;
      padding: 2px 8px;
      border-radius: 999px;
      background: rgba(124,58,237,0.18);
      color: #c4b5fd;
      white-space: nowrap;
    }
    .pchat__chip--active {
      background: linear-gradient(135deg, rgba(124,58,237,0.55), rgba(99,102,241,0.55));
      color: #fff;
    }
    .pchat__search-input {
      flex: 1 1 auto;
      min-width: 0;
      background: rgba(255,255,255,0.05);
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 999px;
      padding: 4px 10px;
      color: #e2e8f0;
      font: inherit;
      font-size: 12px;
    }
    .pchat__search-input:focus {
      outline: none;
      border-color: rgba(196,181,253,0.7);
    }
    .pchat__chip-btn {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.08);
      color: #cbd5e1;
      border-radius: 999px;
      padding: 2px 10px;
      cursor: pointer;
      font-size: 11px;
    }
    .pchat__error {
      padding: 6px 10px;
      background: rgba(248,113,113,0.12);
      color: #fca5a5;
      font-size: 12px;
    }
    .pchat__scroll {
      flex: 1 1 auto;
      min-height: 0;
      overflow-y: auto;
      padding: 8px 10px;
    }
    .pchat__turn {
      display: flex;
      flex-direction: column;
      gap: 4px;
      padding: 8px 10px;
      margin: 6px 0;
      background: rgba(255,255,255,0.025);
      border: 1px solid rgba(255,255,255,0.05);
      border-radius: 8px;
      color: #e2e8f0;
      transition: background 0.4s ease;
    }
    .pchat__turn--user {
      background: rgba(99,102,241,0.18);
      border-color: rgba(124,58,237,0.5);
    }
    .pchat__turn--event {
      border-style: dashed;
      background: rgba(255,255,255,0.015);
      color: #cbd5e1;
      font-size: 12px;
    }
    .pchat__turn--flash {
      background: rgba(250,204,21,0.18);
      border-color: rgba(250,204,21,0.5);
    }
    .pchat__turn-head {
      display: flex;
      gap: 8px;
      align-items: baseline;
      font-size: 11px;
      color: #94a3b8;
    }
    .pchat__author {
      color: #c4b5fd;
      font-weight: 600;
      text-transform: lowercase;
    }
    .pchat__kind {
      font-family: ui-monospace, SFMono-Regular, monospace;
      font-size: 10px;
      color: #64748b;
    }
    .pchat__ts { margin-left: auto; }
    .pchat__body {
      font-size: 13px;
      line-height: 1.5;
      word-break: break-word;
    }
    .pchat__body :is(pre, code) {
      font-family: ui-monospace, SFMono-Regular, monospace;
      font-size: 12px;
      background: rgba(0,0,0,0.4);
      padding: 1px 4px;
      border-radius: 4px;
    }
    .pchat__body pre { padding: 8px; overflow-x: auto; }
    .pchat__hint, .pchat__empty {
      padding: 10px;
      color: #94a3b8;
      font-size: 12px;
      text-align: center;
    }
    .pchat__results { display: flex; flex-direction: column; gap: 6px; }
    .pchat__hit {
      display: flex;
      flex-direction: column;
      gap: 4px;
      text-align: left;
      padding: 8px 10px;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.06);
      border-radius: 6px;
      color: #e2e8f0;
      cursor: pointer;
      font: inherit;
    }
    .pchat__hit:hover { background: rgba(124,58,237,0.18); }
    .pchat__hit-meta { display: flex; gap: 8px; align-items: baseline; font-size: 11px; }
    .pchat__hit-snippet {
      font-size: 12px;
      line-height: 1.4;
      color: #cbd5e1;
    }
    .pchat__hit-snippet ::ng-deep mark,
    .pchat__hit-snippet mark {
      background: rgba(250,204,21,0.35);
      color: #fde68a;
      padding: 0 2px;
      border-radius: 2px;
    }
    .pchat__spacer { width: 100%; }
  `],
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
