import {
  ApplicationRef,
  ComponentRef,
  EnvironmentInjector,
  Injectable,
  createComponent,
  inject,
} from '@angular/core';
import { TaskStatusCardComponent } from './task-status-card.component';
import type { JobInfo } from '../../models/task.model';

const GAP = 8;
const VIEWPORT_PAD = 6;

interface Placement {
  side: 'right' | 'bottom' | 'top' | 'left';
  left: number;
  top: number;
}

/**
 * Singleton popover host for `<app-task-status-card>`. Mounts one Angular
 * component instance into a fixed-position wrapper on `document.body` and
 * positions it next to the anchor element. Re-used by:
 *   - Open-tabs hover (Explorer)
 *   - Job-card hover (board)
 *   - any future place that wants a TaskStatusCard popover.
 *
 * The service is intentionally minimal: callers drive `show()` / `hide()`
 * via the `[appTaskStatusPopover]` directive; the directive owns hover
 * timing and ensures the popover is anchored to its host.
 */
@Injectable({ providedIn: 'root' })
export class TaskStatusCardPopover {
  private readonly appRef = inject(ApplicationRef);
  private readonly envInjector = inject(EnvironmentInjector);

  private host: HTMLDivElement | null = null;
  private cardRef: ComponentRef<TaskStatusCardComponent> | null = null;
  private currentAnchor: HTMLElement | null = null;
  private hoverIntent = false;

  show(anchor: HTMLElement, job: JobInfo): void {
    const host = this.ensureHost();
    const card = this.ensureCard();
    this.currentAnchor = anchor;
    card.setInput('job', job);
    card.setInput('variant', 'popover');
    // The card component's host metadata declares `data-testid="task-status-card"`,
    // which overwrites the wrapper host's `task-status-card-popover` testid the
    // moment the component is created. Re-stamp after the card lands so the
    // wrapper stays addressable from outside (Playwright, console queries).
    host.setAttribute('data-testid', 'task-status-card-popover');

    // We move the host off-screen (rather than setting inline
    // `visibility/opacity`) for the size-measurement pass — inline styles
    // would override the `task-status-card-host--visible` class's visibility
    // declarations later and leave the popover painted-but-invisible.
    host.style.left = '-9999px';
    host.style.top = '0px';
    host.classList.add('task-status-card-host--visible');

    // Force a layout pass so we know the natural size.
    card.changeDetectorRef.detectChanges();

    const place = this.computePlacement(anchor, host);
    host.style.left = `${Math.round(place.left)}px`;
    host.style.top = `${Math.round(place.top)}px`;
    host.dataset['placement'] = place.side;
  }

  hide(anchor: HTMLElement | null): void {
    if (anchor && this.currentAnchor && anchor !== this.currentAnchor) return;
    this.hoverIntent = false;
    if (!this.host) return;
    this.host.classList.remove('task-status-card-host--visible');
    this.currentAnchor = null;
  }

  /**
   * Direct-hover handlers on the popover element so a user can move the
   * pointer from the trigger onto the card without it instantly fading
   * out. The trigger directive calls `markHoverEnter` / `markHoverLeave`
   * on its own mouseenter/leave; the host calls the same when the pointer
   * crosses the card itself.
   */
  markHoverEnter(): void {
    this.hoverIntent = true;
  }

  markHoverLeave(anchor: HTMLElement | null, immediate = false): void {
    if (immediate) {
      this.hide(anchor);
      return;
    }
    this.hoverIntent = false;
    // Defer hide so a quick re-enter (mouse moved between trigger and card)
    // does not cause a flash. The directive handles the 0ms / 80ms timing.
    queueMicrotask(() => {
      if (!this.hoverIntent) this.hide(anchor);
    });
  }

  private ensureHost(): HTMLDivElement {
    if (this.host && document.body.contains(this.host)) return this.host;
    const host = document.createElement('div');
    host.className = 'task-status-card-host';
    host.setAttribute('data-testid', 'task-status-card-popover');
    host.addEventListener('mouseenter', () => this.markHoverEnter());
    host.addEventListener('mouseleave', () => this.markHoverLeave(this.currentAnchor));
    document.body.appendChild(host);
    this.host = host;
    return host;
  }

  private ensureCard(): ComponentRef<TaskStatusCardComponent> {
    if (this.cardRef && this.host && this.cardRef.location.nativeElement.isConnected) {
      return this.cardRef;
    }
    const ref = createComponent(TaskStatusCardComponent, {
      environmentInjector: this.envInjector,
      hostElement: this.host!,
    });
    this.appRef.attachView(ref.hostView);
    this.cardRef = ref;
    return ref;
  }

  private computePlacement(anchor: HTMLElement, host: HTMLDivElement): Placement {
    const aRect = anchor.getBoundingClientRect();
    const hRect = host.getBoundingClientRect();
    const vw = window.innerWidth;
    const vh = window.innerHeight;

    const order: Placement['side'][] = ['right', 'bottom', 'left', 'top'];
    for (const side of order) {
      const cand = this.placeSide(side, aRect, hRect, vw, vh);
      if (cand) return cand;
    }
    return this.placeSide(order[0], aRect, hRect, vw, vh, true)!;
  }

  private placeSide(
    side: Placement['side'],
    a: DOMRect,
    h: DOMRect,
    vw: number,
    vh: number,
    force = false,
  ): Placement | null {
    let top = 0;
    let left = 0;
    switch (side) {
      case 'right':
        top = a.top;
        left = a.right + GAP;
        break;
      case 'left':
        top = a.top;
        left = a.left - h.width - GAP;
        break;
      case 'bottom':
        top = a.bottom + GAP;
        left = a.left;
        break;
      case 'top':
        top = a.top - h.height - GAP;
        left = a.left;
        break;
    }
    const fitsVertically = top >= VIEWPORT_PAD && top + h.height <= vh - VIEWPORT_PAD;
    const fitsHorizontally = left >= VIEWPORT_PAD && left + h.width <= vw - VIEWPORT_PAD;
    const fits =
      side === 'left' || side === 'right' ? fitsHorizontally : fitsVertically;
    if (!fits && !force) return null;

    if (left < VIEWPORT_PAD) left = VIEWPORT_PAD;
    if (top < VIEWPORT_PAD) top = VIEWPORT_PAD;
    if (left + h.width > vw - VIEWPORT_PAD) left = vw - VIEWPORT_PAD - h.width;
    if (top + h.height > vh - VIEWPORT_PAD) top = vh - VIEWPORT_PAD - h.height;
    return { side, left, top };
  }
}
