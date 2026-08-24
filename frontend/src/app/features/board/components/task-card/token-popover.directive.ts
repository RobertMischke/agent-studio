import { Directive, ElementRef, HostListener, OnDestroy, inject } from '@angular/core';
import {
  ConnectedOverlayPositionRef,
  OverlayPortalRef,
  OverlayPortalService,
} from '../../../../services/overlay-portal.service';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { TokenPopoverHandle, TokenPopoverRegistry } from './token-popover-registry.service';

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
 *
 * Exclusivity + dismissal (AGT-2675): hovering across many cards used to
 * leave a trail of popovers open at once, permanently, stacked over card
 * content. `TokenPopoverRegistry` closes whatever popover was previously
 * open the moment a new one opens, and every open popover now also closes
 * on outside click/tap, Escape (via the shared `ModalStackService`), any
 * scroll (lane or window), and the anchor card leaving the viewport.
 */
@Directive({
  selector: '[appTokenPopover]',
  standalone: true,
})
export class TokenPopoverDirective implements OnDestroy, TokenPopoverHandle {
  private static readonly GAP = 8;
  private static readonly VIEWPORT_PAD = 6;
  /** Grace period so the pointer can cross the trigger→popover gap without it closing. */
  private static readonly CLOSE_DELAY_MS = 120;

  private readonly host = inject(ElementRef<HTMLElement>).nativeElement;
  private readonly overlayPortal = inject(OverlayPortalService);
  private readonly registry = inject(TokenPopoverRegistry);
  private readonly modalStack = inject(ModalStackService);
  private popoverEl: HTMLElement | null = null;
  private portalRef: OverlayPortalRef | null = null;
  private positionRef: ConnectedOverlayPositionRef | null = null;
  private closeTimer: ReturnType<typeof setTimeout> | null = null;
  private intersectionObserver: IntersectionObserver | null = null;
  private closeModalStackEntry: (() => void) | null = null;
  private armDismissTimer: ReturnType<typeof setTimeout> | null = null;

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
    this.registry.open(this);
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
      preferredPlacement: 'above',
      alignment: 'end',
      gap: TokenPopoverDirective.GAP,
      viewportPadding: TokenPopoverDirective.VIEWPORT_PAD,
    });
    this.attachDismissListeners();
  }

  /** Public per `TokenPopoverHandle` — the registry calls this on any card whose popover is not the one just opened. */
  close(): void {
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
    this.detachDismissListeners();
    this.registry.close(this);
  }

  /**
   * Everything that dismisses the popover without the pointer ever leaving
   * it: a click/tap outside both the trigger and the panel, Escape (routed
   * through the shared modal stack so it does not also close an unrelated
   * overlay underneath), any scroll (the lane's own scroll container or the
   * window — both surface as a capture-phase `scroll` on `window`, see
   * `OverlayPortalService.watchConnectedPosition`), and the anchor card
   * scrolling/being laid out out of the viewport.
   *
   * Escape registers immediately (a deliberate keypress can't race the open
   * itself). Outside-click, scroll, and the viewport-exit observer arm after
   * a short grace delay instead: opening a popover can itself follow (or be
   * bundled with) a synchronous scroll — e.g. the browser/automation
   * scrolling the anchor into view right before the hover that opens it —
   * and wiring these up eagerly would let that same scroll immediately close
   * the popover it was in the middle of opening.
   */
  private attachDismissListeners(): void {
    this.closeModalStackEntry ??= this.modalStack.push('token-popover', () => {
      this.close();
    });
    if (this.armDismissTimer !== null) return;
    this.armDismissTimer = setTimeout(() => {
      this.armDismissTimer = null;
      document.addEventListener('pointerdown', this.outsideCloseBound, true);
      window.addEventListener('scroll', this.scrollCloseBound, true);
      if (!this.intersectionObserver && typeof IntersectionObserver !== 'undefined') {
        this.intersectionObserver = new IntersectionObserver((entries) => {
          const entry = entries[entries.length - 1];
          if (entry && !entry.isIntersecting) this.close();
        }, { threshold: 0 });
        this.intersectionObserver.observe(this.host);
      }
    }, TokenPopoverDirective.CLOSE_DELAY_MS);
  }

  private detachDismissListeners(): void {
    if (this.armDismissTimer !== null) {
      clearTimeout(this.armDismissTimer);
      this.armDismissTimer = null;
    }
    document.removeEventListener('pointerdown', this.outsideCloseBound, true);
    window.removeEventListener('scroll', this.scrollCloseBound, true);
    this.intersectionObserver?.disconnect();
    this.intersectionObserver = null;
    this.closeModalStackEntry?.();
    this.closeModalStackEntry = null;
  }

  private readonly outsideCloseBound = (event: Event): void => {
    const target = event.target as Node | null;
    if (!target) return;
    if (this.host.contains(target)) return;
    if (this.popoverEl?.contains(target)) return;
    this.close();
  };

  private readonly scrollCloseBound = (event: Event): void => {
    const target = event.target as Node | null;
    // The popover's own overflow (long "per run" tables) fires a `scroll`
    // event too — that is not the "lane/board scrolled" case this exists
    // to handle, so it must not close the panel out from under the user.
    if (target && this.popoverEl?.contains(target)) return;
    this.close();
  };

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
