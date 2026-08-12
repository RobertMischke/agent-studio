import { Directive, ElementRef, HostListener, OnDestroy, inject } from '@angular/core';
import {
  ConnectedOverlayPositionRef,
  OverlayPortalRef,
  OverlayPortalService,
} from '../../../../services/overlay-portal.service';

/**
 * Lifts the token-usage popover into the central body overlay layer so it can
 * never be clipped by the card.
 *
 * The card sets both `overflow: hidden` and `content-visibility: auto`
 * (native virtualisation); the latter implies paint containment, which clips
 * *every* descendant — including `position: fixed` ones — to the card's box.
 * The lane is a scroll container on top of that. An in-flow absolutely
 * positioned popover therefore gets cut off at the first card/panel edge.
 *
 * The directive routes the existing template element through
 * `OverlayPortalService`, which appends it under the shared body-level overlay
 * root. Positioning also goes through the service so flip / clamp behaviour is
 * shared with menus, model pickers, and prompt popovers.
 *
 * Apply on the wrapper that contains the trigger + the `[data-token-popover]`
 * element. The popover element MUST start with the `hidden` attribute in markup:
 * this directive only toggles `pop.hidden`, it does not own the initial state.
 * Without it every card paints its `position: fixed` popover at its static
 * position (off the right viewport edge) until it is first hovered (ASS-1700).
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
  private readonly overlayPortal = inject(OverlayPortalService);
  private popoverEl: HTMLElement | null = null;
  private portalRef: OverlayPortalRef | null = null;
  private positionRef: ConnectedOverlayPositionRef | null = null;
  private closeTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnDestroy(): void {
    this.cancelClose();
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
    if (!pop) return;
    pop.hidden = false;
    pop.style.left = '-9999px';
    pop.style.top = '0px';
    this.portalRef ??= this.overlayPortal.attachPanel(pop);
    pop.addEventListener('mouseenter', this.cancelCloseBound);
    pop.addEventListener('mouseleave', this.scheduleCloseBound);
    pop.addEventListener('focusin', this.cancelCloseBound);
    pop.addEventListener('focusout', this.scheduleCloseBound);
    this.positionRef?.dispose();
    this.positionRef = this.overlayPortal.watchConnectedPosition(this.host, pop, {
      preferredPlacement: 'right',
      alignment: 'end',
      gap: TokenPopoverDirective.GAP,
      viewportPadding: TokenPopoverDirective.VIEWPORT_PAD,
    });
  }

  private close(): void {
    const pop = this.popoverEl;
    this.positionRef?.dispose();
    this.positionRef = null;
    if (pop) {
      pop.removeEventListener('mouseenter', this.cancelCloseBound);
      pop.removeEventListener('mouseleave', this.scheduleCloseBound);
      pop.removeEventListener('focusin', this.cancelCloseBound);
      pop.removeEventListener('focusout', this.scheduleCloseBound);
      pop.hidden = true;
    }
    this.portalRef?.dispose();
    this.portalRef = null;
  }

  private resolvePopover(): HTMLElement | null {
    if (this.popoverEl?.isConnected) return this.popoverEl;
    this.popoverEl = this.host.querySelector('[data-token-popover]');
    if (this.popoverEl) this.popoverEl.hidden = true;
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
}
