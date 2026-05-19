import {
  Directive,
  ElementRef,
  HostListener,
  OnDestroy,
  inject,
  input
} from '@angular/core';
import { TooltipController } from './tooltip.controller';
import { TooltipInput, TooltipPosition, TooltipSeverity } from './tooltip.types';

/**
 * Canonical tooltip directive for the app. Single visual standard, instant
 * hover, lazy singleton DOM. See docs/ui-tooltip-audit.md.
 */
@Directive({
  selector: '[appTooltip]',
  standalone: true
})
export class TooltipDirective implements OnDestroy {
  readonly content = input<TooltipInput>('', { alias: 'appTooltip' });
  readonly tooltipPosition = input<TooltipPosition>('auto');
  readonly tooltipSeverity = input<TooltipSeverity | undefined>(undefined);

  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly controller = inject(TooltipController);
  private touchTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnDestroy(): void {
    this.clearTouchTimer();
    this.controller.hide(this.host.nativeElement);
  }

  @HostListener('mouseenter')
  onEnter(): void {
    this.show();
  }

  @HostListener('mouseleave')
  onLeave(): void {
    this.controller.hide(this.host.nativeElement);
  }

  @HostListener('focusin')
  onFocus(): void {
    this.show();
  }

  @HostListener('focusout')
  onBlur(): void {
    this.controller.hide(this.host.nativeElement);
  }

  @HostListener('click')
  onClick(): void {
    this.controller.hide(this.host.nativeElement);
  }

  @HostListener('touchstart')
  onTouchStart(): void {
    this.show();
    this.clearTouchTimer();
    this.touchTimer = setTimeout(() => {
      this.controller.hide(this.host.nativeElement);
      this.touchTimer = null;
    }, 3000);
  }

  @HostListener('document:touchstart', ['$event'])
  onDocumentTouch(event: TouchEvent): void {
    if (!this.touchTimer) return;
    if (!this.host.nativeElement.contains(event.target as Node)) {
      this.clearTouchTimer();
      this.controller.hide(this.host.nativeElement);
    }
  }

  private show(): void {
    const content = this.content();
    if (!content) return;
    this.controller.show(
      this.host.nativeElement,
      content,
      this.tooltipPosition(),
      this.tooltipSeverity()
    );
  }

  private clearTouchTimer(): void {
    if (this.touchTimer) {
      clearTimeout(this.touchTimer);
      this.touchTimer = null;
    }
  }
}
