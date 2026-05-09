import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  computed,
  effect,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { ClientSummary, TagRegistryEntry } from '../../../models/job.model';
import { TypeFilterOption } from '../filters-dropdown/filters-dropdown.component';

/**
 * VS Code-style right-edge sidesheet that hosts the board's search box and
 * faceted filters in one place. The board reflows live as the user types
 * and toggles facets — there is no separate result list, the kanban itself
 * is the result list. Visibility toggles (compact cards for now; future:
 * empty lanes, rail markers, tool activity) live in their own section so
 * "what do I want to see right now?" controls stay together.
 *
 * The component is a controlled view: it never owns query / facet / view
 * state. All sources of truth stay in `App` so URL hashing and existing
 * filter pills keep working unchanged. The component reads inputs and
 * emits outputs.
 */
@Component({
  selector: 'app-kanban-filter-sidesheet',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <aside class="sheet"
           [class.sheet--open]="open()"
           data-testid="kanban-filter-sidesheet"
           role="dialog"
           aria-label="Board filters">
      <header class="sheet__header">
        <div class="sheet__title-block">
          <span class="sheet__eyebrow">Board</span>
          <h2 class="sheet__title">Filter &amp; view</h2>
        </div>
        <button class="sheet__close"
                type="button"
                (click)="close()"
                title="Close panel (Esc)"
                aria-label="Close filter panel"
                data-testid="kanban-filter-sidesheet-close">✕</button>
      </header>

      <section class="sheet__section">
        <div class="sheet__search-row">
          <span class="sheet__search-icon" aria-hidden="true">🔍</span>
          <input #searchInput
                 type="search"
                 class="sheet__search-input"
                 data-testid="kanban-filter-sidesheet-search"
                 placeholder="Search title, slug, tag, owner…"
                 autocomplete="off"
                 spellcheck="false"
                 [value]="query()"
                 (input)="onInput($event)"
                 (keydown.escape)="onEscape($event)" />
          @if (query().length > 0) {
            <button type="button"
                    class="sheet__search-clear"
                    title="Clear search"
                    aria-label="Clear search"
                    data-testid="kanban-filter-sidesheet-search-clear"
                    (click)="onClear()">×</button>
          }
        </div>
      </section>

      @if (typeOptions().length > 0) {
        <section class="sheet__section">
          <h3 class="sheet__section-title">Task type</h3>
          <div class="sheet__chips">
            @for (opt of typeOptions(); track opt.value) {
              <button type="button"
                      class="sheet__chip"
                      [class.sheet__chip--active]="activeType() === opt.value"
                      [attr.data-testid]="'kanban-filter-type-' + opt.value"
                      [attr.aria-pressed]="activeType() === opt.value"
                      (click)="onPickType(opt.value)">
                <span class="sheet__chip-icon" aria-hidden="true">{{ opt.icon }}</span>
                <span>{{ opt.label }}</span>
              </button>
            }
          </div>
        </section>
      }

      <section class="sheet__section">
        <h3 class="sheet__section-title">
          Tags
          @if (activeTagIds().size > 0) {
            <span class="sheet__count" data-testid="kanban-filter-tag-active-count">{{ activeTagIds().size }} active</span>
          }
        </h3>
        @if (tags().length === 0) {
          <p class="sheet__empty">No tags on this board.</p>
        } @else {
          <ul class="sheet__list" data-testid="kanban-filter-tag-list">
            @for (tag of tags(); track tag.id) {
              <li>
                <label class="sheet__checkbox"
                       [attr.data-testid]="'kanban-filter-tag-' + tag.id"
                       [attr.title]="tag.description || tag.label">
                  <input type="checkbox"
                         [checked]="activeTagIds().has(tag.id)"
                         (change)="onToggleTag(tag.id)" />
                  <span class="sheet__swatch" [style.background]="tag.color" aria-hidden="true"></span>
                  <span class="sheet__checkbox-label">{{ tag.label }}</span>
                </label>
              </li>
            }
          </ul>
        }
      </section>

      <section class="sheet__section">
        <h3 class="sheet__section-title">
          Owner
          @if (activeOwnerId()) {
            <span class="sheet__count">1 active</span>
          }
        </h3>
        @if (owners().length === 0) {
          <p class="sheet__empty">No owners registered.</p>
        } @else {
          <ul class="sheet__list" data-testid="kanban-filter-owner-list">
            <li>
              <label class="sheet__checkbox" data-testid="kanban-filter-owner-all">
                <input type="radio"
                       name="owner-radio"
                       [checked]="activeOwnerId() === null"
                       (change)="onSetOwner(null)" />
                <span class="sheet__swatch sheet__swatch--blank" aria-hidden="true"></span>
                <span class="sheet__checkbox-label">All owners</span>
              </label>
            </li>
            @for (owner of owners(); track owner.id) {
              <li>
                <label class="sheet__checkbox"
                       [attr.data-testid]="'kanban-filter-owner-' + owner.id">
                  <input type="radio"
                         name="owner-radio"
                         [checked]="activeOwnerId() === owner.id"
                         (change)="onSetOwner(owner.id)" />
                  <span class="sheet__swatch"
                        [style.background]="owner.colour || 'rgba(255,255,255,0.12)'"
                        aria-hidden="true">{{ owner.emoji || '·' }}</span>
                  <span class="sheet__checkbox-label">{{ owner.displayName || owner.id }}</span>
                </label>
              </li>
            }
          </ul>
        }
      </section>

      <section class="sheet__section">
        <h3 class="sheet__section-title">Visibility</h3>
        <ul class="sheet__list">
          <li>
            <label class="sheet__checkbox" data-testid="kanban-filter-visibility-compact">
              <input type="checkbox"
                     [checked]="compactCards()"
                     (change)="onToggleCompact()" />
              <span class="sheet__swatch sheet__swatch--blank" aria-hidden="true">▤</span>
              <span class="sheet__checkbox-label">Compact cards (titles only)</span>
            </label>
          </li>
        </ul>
      </section>

      <footer class="sheet__footer" data-testid="kanban-filter-sidesheet-footer">
        <span class="sheet__hits"
              data-testid="kanban-filter-sidesheet-hitcount">{{ hitCount() }} / {{ totalCount() }} jobs match</span>
        <button type="button"
                class="sheet__clear-all"
                data-testid="kanban-filter-sidesheet-clear-all"
                [disabled]="!hasAnyFilter()"
                (click)="onClearAll()">Clear filters</button>
      </footer>
    </aside>
  `,
  styles: [`
    :host {
      display: block;
      width: 0;
      transition: width 0.18s ease;
      overflow: hidden;
      flex: 0 0 auto;
    }
    :host(.is-open) { width: min(320px, 92vw); }

    .sheet {
      width: min(320px, 92vw);
      height: 100%;
      background: #11111b;
      border-left: 1px solid rgba(255,255,255,0.08);
      display: flex;
      flex-direction: column;
      color: #e2e8f0;
      overflow-y: auto;
    }
    .sheet__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      padding: 12px 14px 10px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
      background:
        radial-gradient(circle at top left, rgba(99,102,241,0.14), transparent 60%),
        rgba(255,255,255,0.02);
      position: sticky;
      top: 0;
      z-index: 2;
    }
    .sheet__title-block { display: flex; flex-direction: column; gap: 2px; }
    .sheet__eyebrow {
      font-size: 10px;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: #93c5fd;
      font-weight: 700;
    }
    .sheet__title {
      margin: 0;
      font-size: 14px;
      color: #f8fafc;
      font-weight: 600;
    }
    .sheet__close {
      background: rgba(255,255,255,0.06);
      border: 0;
      color: #cbd5e1;
      width: 26px; height: 26px;
      border-radius: 999px;
      cursor: pointer;
      font-size: 13px;
    }
    .sheet__close:hover { background: rgba(255,255,255,0.12); }

    .sheet__section {
      padding: 12px 14px;
      border-bottom: 1px solid rgba(255,255,255,0.04);
    }
    .sheet__section:last-of-type { border-bottom: 0; }
    .sheet__section-title {
      margin: 0 0 8px;
      font-size: 11px;
      font-weight: 600;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: #94a3b8;
      display: flex;
      justify-content: space-between;
      align-items: baseline;
    }
    .sheet__count {
      font-size: 10px;
      font-weight: 500;
      color: #64748b;
      letter-spacing: 0;
      text-transform: none;
    }

    .sheet__search-row {
      display: flex;
      align-items: center;
      gap: 6px;
      padding: 6px 10px;
      background: rgba(255,255,255,0.05);
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 8px;
    }
    .sheet__search-row:focus-within {
      border-color: rgba(147,197,253,0.6);
      box-shadow: 0 0 0 1px rgba(147,197,253,0.4);
    }
    .sheet__search-icon { opacity: 0.7; font-size: 13px; }
    .sheet__search-input {
      flex: 1 1 auto;
      min-width: 0;
      background: transparent;
      border: 0;
      color: inherit;
      font: inherit;
      padding: 2px 0;
      outline: none;
    }
    .sheet__search-input::placeholder { color: #64748b; }
    .sheet__search-clear {
      background: rgba(255,255,255,0.08);
      border: 0;
      color: #cbd5e1;
      width: 20px; height: 20px;
      border-radius: 999px;
      cursor: pointer;
      font-size: 12px;
      line-height: 1;
    }
    .sheet__search-clear:hover { background: rgba(255,255,255,0.16); }

    .sheet__chips {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .sheet__chip {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 4px 10px;
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 999px;
      color: #cbd5e1;
      font-size: 12px;
      cursor: pointer;
      transition: background 0.12s, border-color 0.12s, color 0.12s;
    }
    .sheet__chip:hover { background: rgba(255,255,255,0.08); }
    .sheet__chip--active {
      background: linear-gradient(135deg, rgba(59,130,246,0.50), rgba(99,102,241,0.50));
      border-color: rgba(147,197,253,0.7);
      color: #ffffff;
      font-weight: 600;
    }
    .sheet__chip-icon { font-size: 12px; }

    .sheet__list {
      list-style: none;
      margin: 0;
      padding: 0;
      display: flex;
      flex-direction: column;
      gap: 2px;
      max-height: 220px;
      overflow-y: auto;
    }
    .sheet__empty {
      margin: 0;
      color: #64748b;
      font-size: 12px;
      font-style: italic;
    }
    .sheet__checkbox {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 4px 6px;
      border-radius: 6px;
      cursor: pointer;
      font-size: 12.5px;
      color: #cbd5e1;
      transition: background 0.1s;
    }
    .sheet__checkbox:hover { background: rgba(255,255,255,0.05); }
    .sheet__checkbox input { margin: 0; cursor: pointer; }
    .sheet__swatch {
      width: 14px;
      height: 14px;
      border-radius: 3px;
      flex: 0 0 auto;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      font-size: 9px;
      color: #ffffff;
      box-shadow: inset 0 0 0 1px rgba(255,255,255,0.10);
    }
    .sheet__swatch--blank {
      background: rgba(255,255,255,0.05);
    }
    .sheet__checkbox-label {
      flex: 1 1 auto;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }

    .sheet__footer {
      margin-top: auto;
      padding: 10px 14px;
      border-top: 1px solid rgba(255,255,255,0.06);
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      font-size: 11.5px;
      background: rgba(255,255,255,0.02);
      position: sticky;
      bottom: 0;
    }
    .sheet__hits { color: #94a3b8; }
    .sheet__clear-all {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.10);
      color: #cbd5e1;
      padding: 4px 10px;
      border-radius: 6px;
      cursor: pointer;
      font-size: 11.5px;
    }
    .sheet__clear-all:hover:not(:disabled) { background: rgba(255,255,255,0.10); }
    .sheet__clear-all:disabled { opacity: 0.4; cursor: not-allowed; }
  `],
  host: {
    '[class.is-open]': 'open()'
  }
})
export class KanbanFilterSidesheetComponent {
  readonly open = input<boolean>(false);
  readonly query = input<string>('');
  readonly typeOptions = input<readonly TypeFilterOption[]>([]);
  readonly activeType = input<string | null>(null);
  readonly tags = input<readonly TagRegistryEntry[]>([]);
  readonly activeTagIds = input<ReadonlySet<string>>(new Set<string>());
  readonly owners = input<readonly ClientSummary[]>([]);
  readonly activeOwnerId = input<string | null>(null);
  readonly compactCards = input<boolean>(false);
  readonly hitCount = input<number>(0);
  readonly totalCount = input<number>(0);
  readonly hasAnyFilter = input<boolean>(false);

  readonly queryChange = output<string>();
  readonly setType = output<string | null>();
  readonly toggleTag = output<string>();
  readonly setOwner = output<string | null>();
  readonly toggleCompactCards = output<void>();
  readonly closed = output<void>();
  readonly clearAll = output<void>();

  private readonly searchInputEl = viewChild<ElementRef<HTMLInputElement>>('searchInput');

  /** Suppress the input loop: when our own emit echoes back via the input
   *  binding we don't want to retrigger the change. Tracked via a signal so
   *  the effect that focuses on open is still pure. */
  private readonly localQuery = signal<string>('');

  constructor() {
    effect(() => {
      if (this.open()) {
        // Defer one tick so the host's `is-open` width transition has
        // started; focusing inside a zero-width host steals focus
        // without scrolling it into view.
        queueMicrotask(() => this.searchInputEl()?.nativeElement.focus());
      }
    });
  }

  onInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.localQuery.set(value);
    this.queryChange.emit(value);
  }

  onClear(): void {
    this.queryChange.emit('');
    queueMicrotask(() => this.searchInputEl()?.nativeElement.focus());
  }

  onEscape(event: Event): void {
    event.preventDefault();
    this.close();
  }

  onPickType(value: string): void {
    if (this.activeType() === value) {
      this.setType.emit(null);
    } else {
      this.setType.emit(value);
    }
  }

  onToggleTag(id: string): void {
    this.toggleTag.emit(id);
  }

  onSetOwner(id: string | null): void {
    this.setOwner.emit(id);
  }

  onToggleCompact(): void {
    this.toggleCompactCards.emit();
  }

  onClearAll(): void {
    this.clearAll.emit();
  }

  close(): void {
    this.closed.emit();
  }

  @HostListener('document:keydown.escape', ['$event'])
  onDocumentEscape(event: Event): void {
    if (!this.open()) return;
    // If the user is typing in our search field, the field handles Esc;
    // anything else (a stray focus) closes the sidesheet outright.
    const target = event.target as HTMLElement | null;
    if (target?.getAttribute('data-testid') === 'kanban-filter-sidesheet-search') return;
    event.preventDefault();
    this.close();
  }
}
