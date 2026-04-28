import { Injectable, OnDestroy, signal } from '@angular/core';

/**
 * Provides a wall-clock signal that ticks every 15 s. Use in templates
 * that render relative times (e.g. "in 73 min", "resets in 2 h"); the
 * relative formatter must read this signal (or the value passed by the
 * component that read it) so its result stays stable within a single
 * change-detection cycle.
 *
 * Reading `Date.now()` directly inside template-bound methods causes
 * NG0100 in dev mode whenever the value crosses a granularity boundary
 * (minute, hour) between the regular and the `checkNoChanges` pass.
 */
@Injectable({ providedIn: 'root' })
export class NowTickService implements OnDestroy {
  private readonly _now = signal(Date.now());
  private readonly timer = setInterval(() => this._now.set(Date.now()), 15_000);

  readonly now = this._now.asReadonly();

  ngOnDestroy(): void {
    clearInterval(this.timer);
  }
}
