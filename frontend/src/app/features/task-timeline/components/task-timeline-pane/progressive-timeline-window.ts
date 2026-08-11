import { signal } from '@angular/core';

/** Progressive, variable-height-safe window over the newest timeline rows. */
export class ProgressiveTimelineWindow<T> {
  private readonly pageSize = 50;
  private readonly visibleLimit = signal(50);
  private previousSource = { firstIdentity: '', total: 0 };

  constructor(private readonly identity: (item: T) => string) {}

  sync(items: readonly T[]): void {
    const next = {
      firstIdentity: items[0] ? this.identity(items[0]) : '',
      total: items.length,
    };
    const previous = this.previousSource;
    this.previousSource = next;
    if (next.firstIdentity !== previous.firstIdentity || next.total < previous.total) {
      this.visibleLimit.set(this.pageSize);
    } else if (next.total > previous.total && this.visibleLimit() > this.pageSize) {
      this.visibleLimit.update(limit => limit + next.total - previous.total);
    }
  }

  visible(items: readonly T[]): readonly T[] {
    return items.slice(Math.max(0, items.length - this.visibleLimit()));
  }

  olderCount(total: number): number {
    return Math.max(0, total - this.visibleLimit());
  }

  nextPageSize(total: number): number {
    return Math.min(this.pageSize, this.olderCount(total));
  }

  loadOlder(total: number): void {
    this.visibleLimit.update(limit => Math.min(total, limit + this.pageSize));
  }
}
