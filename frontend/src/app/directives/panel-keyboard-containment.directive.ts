import { AfterViewInit, Directive, ElementRef, inject } from '@angular/core';

const PANEL_SCROLL_KEYS = new Set([
  'ArrowUp',
  'ArrowDown',
  'PageUp',
  'PageDown',
  'Home',
  'End',
]);

/**
 * Keeps native vertical scrolling inside a focusable panel surface.
 *
 * This follows the CAC-22 chat contract: stop propagation so parent keyboard
 * navigation cannot consume the key, but do not prevent the browser default
 * that scrolls the focused overflow owner.
 */
@Directive({
  selector: '[appPanelKeyboardContainment]',
  standalone: true,
  host: {
    tabindex: '0',
    '(keydown)': 'onKeydown($event)',
  },
})
export class PanelKeyboardContainmentDirective implements AfterViewInit {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;

  ngAfterViewInit(): void {
    queueMicrotask(() => {
      const scope = this.host.closest<HTMLElement>('[data-panel-keyboard-scope]');
      if (!scope || scope.contains(document.activeElement)) return;

      const preferred = scope.querySelector<HTMLElement>('[data-panel-keyboard-autofocus]');
      const firstSurface = scope.querySelector<HTMLElement>('[appPanelKeyboardContainment]');
      if (this.host === (preferred ?? firstSurface)) {
        this.host.focus({ preventScroll: true });
      }
    });
  }

  onKeydown(event: KeyboardEvent): void {
    if (PANEL_SCROLL_KEYS.has(event.key)) event.stopPropagation();
  }
}
