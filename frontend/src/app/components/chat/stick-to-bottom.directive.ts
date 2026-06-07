import {
  AfterViewInit,
  Directive,
  ElementRef,
  OnDestroy,
  inject,
  input,
  signal,
} from '@angular/core';

/**
 * Shared stick-to-bottom scroll behaviour for the chat surfaces
 * (`conversation-view`, and — once ASS-673 lands — the orchestrator
 * `chat` and the activity log). Three near-identical hand-rolled copies of
 * this logic existed before; this directive is the single extraction the
 * Job-Details cluster asked for.
 *
 * Attach it to the element whose growing content should keep the latest
 * row in view:
 *
 *   <section class="conv" appStickToBottom #stick="stickToBottom"> … </section>
 *
 * The directive resolves the *actual* scroll container at runtime — the
 * nearest scrollable ancestor (or the host itself when it scrolls). That
 * matters for the embedded conversation view, whose own host does not
 * scroll: the protocol pane's `.pane__body` is the real scroller, several
 * levels up. A `ResizeObserver` on the host detects content growth (new
 * agent lines streaming in) and re-pins the container to the bottom — but
 * only while {@link stuck} is true. The moment the user scrolls up past a
 * small threshold the directive releases, and never yanks them back down
 * until they return to the bottom (or call {@link scrollToBottom} via the
 * "jump to latest" affordance).
 *
 * Note on the absence of a spurious-release race: appending DOM content
 * grows `scrollHeight` while `scrollTop` stays put, which does NOT emit a
 * `scroll` event — only an actual position change does. So the
 * ResizeObserver re-pin and the scroll-driven release never fight; the only
 * programmatic write we make is guarded by {@link suppressScrollEvent}.
 */
@Directive({
  selector: '[appStickToBottom]',
  standalone: true,
  exportAs: 'stickToBottom',
})
export class StickToBottomDirective implements AfterViewInit, OnDestroy {
  /** Distance from the bottom (px) within which the view counts as "stuck". */
  readonly threshold = input(24, { alias: 'stickThreshold' });

  private readonly host = inject(ElementRef<HTMLElement>);

  private readonly _stuck = signal(true);
  /** True while the latest row is pinned in view; false once the user scrolls up. */
  readonly stuck = this._stuck.asReadonly();

  private container: HTMLElement | null = null;
  private resizeObserver: ResizeObserver | null = null;
  private scrollFrame: number | null = null;
  private suppressScrollEvent = false;
  private editableFocused = false;
  private readonly onScroll = (): void => this.handleScroll();
  private readonly onFocusIn = (event: FocusEvent): void => this.handleFocusIn(event);
  private readonly onFocusOut = (): void => this.handleFocusOut();

  ngAfterViewInit(): void {
    this.container = this.resolveScrollContainer();
    if (this.container) {
      this.container.addEventListener('scroll', this.onScroll, { passive: true });
      this.container.addEventListener('focusin', this.onFocusIn);
      this.container.addEventListener('focusout', this.onFocusOut);
    }
    if (typeof ResizeObserver !== 'undefined') {
      this.resizeObserver = new ResizeObserver(() => this.onContentResize());
      this.resizeObserver.observe(this.host.nativeElement);
      if (this.container) this.resizeObserver.observe(this.container);
    }
    // Initial pin: land on the newest row the first time content paints.
    this.scheduleScrollToBottom();
  }

  ngOnDestroy(): void {
    if (this.container) {
      this.container.removeEventListener('scroll', this.onScroll);
      this.container.removeEventListener('focusin', this.onFocusIn);
      this.container.removeEventListener('focusout', this.onFocusOut);
    }
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
    if (this.scrollFrame !== null && typeof cancelAnimationFrame !== 'undefined') {
      cancelAnimationFrame(this.scrollFrame);
      this.scrollFrame = null;
    }
  }

  /** Re-pin to the bottom and resume sticking (the "jump to latest" action). */
  scrollToBottom(): void {
    this._stuck.set(true);
    this.scheduleScrollToBottom();
  }

  private onContentResize(): void {
    if (this.editableFocused) {
      return;
    }
    if (!this._stuck()) return;
    this.scheduleScrollToBottom();
  }

  private handleScroll(): void {
    if (this.suppressScrollEvent) return;
    const el = this.container;
    if (!el) return;
    const distanceFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
    this._stuck.set(distanceFromBottom <= this.threshold());
  }

  private handleFocusIn(event: FocusEvent): void {
    const target = event.target;
    this.editableFocused = target instanceof HTMLElement && this.isEditable(target);
    if (this.editableFocused) {
      this.cancelPendingScroll();
    }
  }

  private handleFocusOut(): void {
    // Focus may move between textarea descendants during the same turn.
    // Defer the check so the next activeElement is settled before resuming.
    requestAnimationFrame(() => {
      const active = document.activeElement;
      this.editableFocused = active instanceof HTMLElement && this.isEditable(active);
      this.handleScroll();
    });
  }

  private isEditable(el: HTMLElement): boolean {
    if (el.isContentEditable) return true;
    const tag = el.tagName.toLowerCase();
    return tag === 'textarea' || tag === 'input' || tag === 'select';
  }

  private scheduleScrollToBottom(): void {
    if (typeof requestAnimationFrame === 'undefined') return;
    // Coalesce to a single pin per frame: a streaming tick can grow the
    // content several times in quick succession, but we only want one
    // scrollTop write, after the browser has laid out the new rows.
    this.cancelPendingScroll();
    this.scrollFrame = requestAnimationFrame(() => {
      this.scrollFrame = null;
      const el = this.container ?? (this.container = this.resolveScrollContainer());
      if (!el) return;
      if (this.editableFocused) return;
      if (!this._stuck()) return;
      // The write fires exactly one scroll event (no smooth behaviour);
      // suppress it so handleScroll doesn't misread the transient position
      // and flip `stuck` off. Cleared on the next frame.
      this.suppressScrollEvent = true;
      el.scrollTop = el.scrollHeight;
      requestAnimationFrame(() => {
        this.suppressScrollEvent = false;
      });
    });
  }

  private cancelPendingScroll(): void {
    if (this.scrollFrame === null || typeof cancelAnimationFrame === 'undefined') return;
    cancelAnimationFrame(this.scrollFrame);
    this.scrollFrame = null;
  }

  /**
   * Nearest scrollable element: the host itself if it scrolls, otherwise the
   * first ancestor whose computed `overflow-y` allows scrolling. Falls back
   * to the document scrolling element so a detached/edge layout still pins.
   */
  private resolveScrollContainer(): HTMLElement | null {
    if (typeof window === 'undefined') return null;
    let el: HTMLElement | null = this.host.nativeElement;
    while (el) {
      const oy = getComputedStyle(el).overflowY;
      if (oy === 'auto' || oy === 'scroll' || oy === 'overlay') return el;
      el = el.parentElement;
    }
    return (document.scrollingElement as HTMLElement | null) ?? null;
  }
}
