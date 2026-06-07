import { Directive, ElementRef, OnDestroy, OnInit, effect, inject, input } from '@angular/core';
import {
  ConnectedOverlayPositionRef,
  OverlayAlignment,
  OverlayPlacement,
  OverlayPortalRef,
  OverlayPortalService,
} from '../services/overlay-portal.service';

/**
 * Body-portaled, anchor-positioned overlay primitive.
 *
 * Apply this to the floating panel element itself. The directive hoists the
 * panel into the central overlay root, pins it to the supplied anchor, flips
 * when the preferred side does not fit, and keeps the position fresh on
 * scroll, resize, and content-size changes.
 */
@Directive({
  selector: '[appConnectedOverlay]',
  standalone: true,
})
export class ConnectedOverlayDirective implements OnInit, OnDestroy {
  readonly appConnectedOverlay = input<HTMLElement | null>(null);
  readonly connectedOverlayPlacement = input<OverlayPlacement>('below');
  readonly connectedOverlayAlignment = input<OverlayAlignment>('start');
  readonly connectedOverlayGap = input(6);
  readonly connectedOverlayViewportPadding = input(8);
  readonly connectedOverlayMinWidth = input<number | null>(null);

  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly overlayPortal = inject(OverlayPortalService);
  private portalRef: OverlayPortalRef | null = null;
  private positionRef: ConnectedOverlayPositionRef | null = null;

  private readonly positionEffect = effect(() => {
    const anchor = this.appConnectedOverlay();
    this.connectedOverlayPlacement();
    this.connectedOverlayAlignment();
    this.connectedOverlayGap();
    this.connectedOverlayViewportPadding();
    this.connectedOverlayMinWidth();
    queueMicrotask(() => this.bindPosition(anchor));
  });

  ngOnInit(): void {
    this.portalRef = this.overlayPortal.attachPanel(this.host.nativeElement);
    this.bindPosition(this.appConnectedOverlay());
  }

  ngOnDestroy(): void {
    this.positionRef?.dispose();
    this.positionRef = null;
    this.portalRef?.dispose();
    this.portalRef = null;
  }

  private bindPosition(anchor: HTMLElement | null): void {
    if (!this.portalRef || !anchor) return;
    this.positionRef?.dispose();
    this.positionRef = this.overlayPortal.watchConnectedPosition(anchor, this.host.nativeElement, {
      preferredPlacement: this.connectedOverlayPlacement(),
      alignment: this.connectedOverlayAlignment(),
      gap: this.connectedOverlayGap(),
      viewportPadding: this.connectedOverlayViewportPadding(),
      minWidth: this.connectedOverlayMinWidth(),
    });
  }
}
