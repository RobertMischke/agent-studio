import { TooltipDirective } from '../../../../components/tooltip';
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

/**
 * Header-mounted board search affordance. Collapses into a 24px icon when
 * idle so the header chrome stays calm; expands inline into a search input
 * on click, '/' shortcut, or focus. When the input is non-empty and the
 * user clicks away, the affordance collapses to a slim chip showing the
 * active query plus an × clear button (the filter remains active).
 *
 * The component never owns the query state - it reads `query()` from the
 * parent and emits `queryChange` so the kanban filter pipeline keeps the
 * single source of truth in `App.searchQuery`.
 *
 * The component renders only when the parent decides the board is the
 * active view (detail page, project chat, update center hidden); '/' /
 * Escape shortcuts therefore listen on document only while the icon is
 * mounted, which is exactly the kanban context.
 */
@Component({
  selector: 'app-board-search-icon',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './board-search-icon.component.html',
  styleUrls: ['./board-search-icon.component.scss'],
})
export class BoardSearchIconComponent {
  readonly query = input<string>('');
  readonly queryChange = output<string>();

  readonly expanded = signal(false);
  readonly hasQuery = computed(() => this.query().trim().length > 0);

  private readonly inputEl = viewChild<ElementRef<HTMLInputElement>>('input');

  constructor() {
    // After the input is rendered (expansion just toggled), focus it.
    effect(() => {
      if (this.expanded()) {
        queueMicrotask(() => this.inputEl()?.nativeElement.focus());
      }
    });
  }

  expand(): void {
    if (!this.expanded()) this.expanded.set(true);
  }

  collapse(): void {
    this.expanded.set(false);
  }

  onInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.queryChange.emit(value);
  }

  onEscape(event: Event): void {
    // Esc always collapses. If a query is set the chip remains visible as
    // a reminder that the board is still actively filtered; otherwise the
    // input collapses to the bare icon.
    event.preventDefault();
    this.collapse();
  }

  onBlur(): void {
    // Defer so the clear-button click handler runs first; otherwise the
    // blur triggered by the click would collapse the input before the
    // click resets the query.
    setTimeout(() => this.collapse(), 0);
  }

  onChipClick(): void {
    this.expand();
  }

  onChipClear(event: Event): void {
    event.stopPropagation();
    this.queryChange.emit('');
    this.collapse();
  }

  clearAndCollapse(event: Event): void {
    event.stopPropagation();
    event.preventDefault();
    this.queryChange.emit('');
    this.collapse();
  }

  /**
   * Global "/" shortcut, classic search-focus binding. Listens on document
   * only while this component is mounted - which is exactly the kanban
   * board context (the parent unmounts the icon on detail / chat / update
   * center). Ignores the keystroke when the user is already typing in any
   * input/textarea/contenteditable so it never steals focus from forms.
   */
  @HostListener('document:keydown', ['$event'])
  onDocumentKeydown(event: KeyboardEvent): void {
    if (event.key !== '/') return;
    if (event.ctrlKey || event.metaKey || event.altKey) return;
    const target = event.target as HTMLElement | null;
    if (target) {
      const tag = target.tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
      if (target.isContentEditable) return;
    }
    event.preventDefault();
    this.expand();
  }
}
