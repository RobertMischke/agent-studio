import { Directive, ElementRef, HostBinding, effect, inject, input } from '@angular/core';

/**
 * Shared pending-state contract for native buttons. Consumers keep their
 * existing button styling and content while this directive adds the common
 * disabled, busy, spinner and pending-label behaviour.
 */
@Directive({
  selector: 'button[appPendingButton]',
  standalone: true,
})
export class PendingButtonDirective {
  private readonly button = inject<ElementRef<HTMLButtonElement>>(ElementRef).nativeElement;
  private disabledBeforePending = false;
  private wasPending = false;
  readonly pending = input(false, { alias: 'appPendingButton' });
  readonly pendingLabel = input('Working…');

  constructor() {
    effect(() => {
      if (this.pending()) {
        if (!this.wasPending) this.disabledBeforePending = this.button.disabled;
        this.wasPending = true;
        this.button.disabled = true;
      } else if (this.wasPending) {
        this.wasPending = false;
        this.button.disabled = this.disabledBeforePending;
      }
    });
  }

  @HostBinding('class.app-pending-button--pending')
  get pendingClass(): boolean { return this.pending(); }

  @HostBinding('attr.aria-busy')
  get ariaBusy(): string | null { return this.pending() ? 'true' : null; }

  @HostBinding('attr.data-pending-label')
  get label(): string | null { return this.pending() ? this.pendingLabel() : null; }

}
