import { Directive, ElementRef, HostListener, OnDestroy, inject } from '@angular/core';

/**
 * Lifts the token-usage popover into the browser **top layer** so it can never
 * be clipped by the card.
 *
 * The card sets both `overflow: hidden` and `content-visibility: auto`
 * (native virtualisation); the latter implies paint containment, which clips
 * *every* descendant — including `position: fixed` ones — to the card's box.
 * The lane is a scroll container on top of that. An in-flow absolutely
 * positioned popover therefore gets cut off at the first card/panel edge.
 *
 * The native Popover API is the only mechanism that escapes all of those at
 * once: `showPopover()` promotes the element to the top layer (viewport is its
 * containing block, no ancestor overflow/containment/transform applies). This
 * directive owns the hover/focus lifecycle and positions the popover next to
 * its trigger, clamped to the viewport so it stays fully visible at any edge.
 *
 * Apply on the wrapper that contains the trigger + the `[data-token-popover]`
 * element. The popover element must carry the `popover` attribute in markup.
 */
@Directive({
  selector: '[appTokenPopover]',
  standalone: true,
})
export class TokenPopoverDirective implements OnDestroy {
  private static readonly GAP = 8;
  private static readonly VIEWPORT_PAD = 6;
  /** Grace period so the pointer can cross the trigger→popover gap without it closing. */
  private static readonly CLOSE_DELAY_MS = 120;

  private readonly host = inject(ElementRef<HTMLElement>).nativeElement;
  private popoverEl: HTMLElement | null = null;
  private closeTimer: ReturnType<typeof setTimeout> | null = null;
  private readonly reposition = () => this.position();

  ngOnDestroy(): void {
    this.cancelClose();
    this.detachReposition();
    this.close();
  }

  @HostListener('mouseenter')
  @HostListener('focusin')
  onOpen(): void {
    this.open();
  }

  @HostListener('mouseleave')
  @HostListener('focusout')
  onLeave(): void {
    this.scheduleClose();
  }

  private open(): void {
    this.cancelClose();
    const pop = this.resolvePopover();
    if (!pop || typeof pop.showPopover !== 'function') return;
    // Park off-screen before showing so the UA's default centred placement
    // never paints — measurement + final placement happen synchronously
    // before the browser's next paint.
    pop.style.left = '-9999px';
    pop.style.top = '0';
    if (!pop.matches(':popover-open')) {
      try {
        pop.showPopover();
      } catch {
        return; // already open elsewhere / unsupported state — bail quietly
      }
      // Keep open while the pointer rests on the popover itself (it renders
      // detached from the trigger, so the trigger's mouseleave fires when the
      // pointer crosses the gap).
      pop.addEventListener('mouseenter', this.cancelCloseBound);
      pop.addEventListener('mouseleave', this.scheduleCloseBound);
    }
    this.attachReposition();
    this.position();
  }

  private close(): void {
    const pop = this.popoverEl;
    if (pop?.matches(':popover-open')) {
      try {
        pop.hidePopover();
      } catch {
        /* not open — ignore */
      }
    }
    this.detachReposition();
  }

  private position(): void {
    const pop = this.popoverEl;
    if (!pop?.matches(':popover-open')) return;
    const a = this.host.getBoundingClientRect();
    const r = pop.getBoundingClientRect();
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const { GAP, VIEWPORT_PAD: PAD } = TokenPopoverDirective;

    // Preferred placement mirrors the old design: above the trigger, right
    // edges aligned. Flip below when there's no room above.
    let left = a.right - r.width;
    let top = a.top - GAP - r.height;
    if (top < PAD) top = a.bottom + GAP;

    left = this.clamp(left, PAD, vw - PAD - r.width);
    top = this.clamp(top, PAD, vh - PAD - r.height);

    pop.style.left = `${Math.round(left)}px`;
    pop.style.top = `${Math.round(top)}px`;
  }

  private clamp(value: number, min: number, max: number): number {
    if (max < min) return min;
    return Math.min(Math.max(value, min), max);
  }

  private resolvePopover(): HTMLElement | null {
    if (this.popoverEl && this.host.contains(this.popoverEl)) return this.popoverEl;
    this.popoverEl = this.host.querySelector('[data-token-popover]');
    return this.popoverEl;
  }

  private scheduleClose(): void {
    this.cancelClose();
    this.closeTimer = setTimeout(() => {
      this.closeTimer = null;
      this.close();
    }, TokenPopoverDirective.CLOSE_DELAY_MS);
  }

  private cancelClose(): void {
    if (this.closeTimer !== null) {
      clearTimeout(this.closeTimer);
      this.closeTimer = null;
    }
  }

  private readonly cancelCloseBound = () => this.cancelClose();
  private readonly scheduleCloseBound = () => this.scheduleClose();

  private attachReposition(): void {
    window.addEventListener('scroll', this.reposition, true);
    window.addEventListener('resize', this.reposition);
  }

  private detachReposition(): void {
    window.removeEventListener('scroll', this.reposition, true);
    window.removeEventListener('resize', this.reposition);
  }
}
