import {
  Directive,
  ElementRef,
  HostListener,
  OnDestroy,
  inject,
  input,
} from '@angular/core';
import { TaskStatusCardPopover } from './task-status-card-popover.service';
import type { TaskInfo } from '../../models/task.model';

/**
 * Hover trigger that opens an `<app-task-status-card>` in popover mode.
 * Apply on any host whose text might be truncated (open-tabs row,
 * compact task-card) — the directive opens after 500 ms of hover and
 * closes immediately on mouse leave.
 *
 * Disabled when `[appTaskStatusPopover]` is null (no job to render).
 */
@Directive({
  selector: '[appTaskStatusPopover]',
  standalone: true,
})
export class TaskStatusPopoverDirective implements OnDestroy {
  /** The job to render in the popover. `null` disables the trigger. */
  readonly appTaskStatusPopover = input<TaskInfo | null>(null);

  /** Trigger delay in ms; default 500 ms per task spec. */
  readonly appTaskStatusPopoverDelay = input<number>(500);

  /**
   * Whether to *only* trigger when the host text is visually truncated
   * (scrollWidth > clientWidth). Default `true` so static-length titles
   * skip the popover entirely.
   */
  readonly appTaskStatusPopoverOnlyTruncated = input<boolean>(true);

  private readonly hostRef = inject(ElementRef<HTMLElement>);
  private readonly popover = inject(TaskStatusCardPopover);
  private openTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnDestroy(): void {
    this.clearOpenTimer();
    this.popover.hide(this.hostRef.nativeElement);
  }

  @HostListener('mouseenter')
  onEnter(): void {
    this.scheduleOpen();
  }

  @HostListener('mouseleave')
  onLeave(): void {
    this.clearOpenTimer();
    this.popover.markHoverLeave(this.hostRef.nativeElement);
  }

  @HostListener('focusin')
  onFocus(): void {
    this.scheduleOpen();
  }

  @HostListener('focusout')
  onBlur(): void {
    this.clearOpenTimer();
    this.popover.markHoverLeave(this.hostRef.nativeElement, true);
  }

  @HostListener('click')
  onClick(): void {
    // Mouse-click on a tab opens the tab; the popover should disappear
    // so it does not stick over the new tab content.
    this.clearOpenTimer();
    this.popover.hide(this.hostRef.nativeElement);
  }

  private scheduleOpen(): void {
    const job = this.appTaskStatusPopover();
    if (!job) return;
    this.popover.markHoverEnter();
    if (this.appTaskStatusPopoverOnlyTruncated() && !this.isTruncated()) return;
    this.clearOpenTimer();
    this.openTimer = setTimeout(() => {
      this.openTimer = null;
      // Re-check job in case it was nulled between schedule + fire.
      const j = this.appTaskStatusPopover();
      if (j) this.popover.show(this.hostRef.nativeElement, j);
    }, this.appTaskStatusPopoverDelay());
  }

  private clearOpenTimer(): void {
    if (this.openTimer !== null) {
      clearTimeout(this.openTimer);
      this.openTimer = null;
    }
  }

  /**
   * Walk the host + its descendants to detect text that visually overflows
   * its container (ellipsis active). Tabs render their title inside a
   * `[data-truncatable]` child; we look for that first, then fall back to
   * the host itself.
   */
  private isTruncated(): boolean {
    const host = this.hostRef.nativeElement;
    const target = (host.querySelector('[data-truncatable]') as HTMLElement | null) ?? host;
    return target.scrollWidth > target.clientWidth + 1;
  }
}
