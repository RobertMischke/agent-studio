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
import { ClientSummary, TagRegistryEntry } from '../../../../models/job.model';
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
  templateUrl: './kanban-filter-sidesheet.component.html',
  styleUrl: './kanban-filter-sidesheet.component.scss',
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
