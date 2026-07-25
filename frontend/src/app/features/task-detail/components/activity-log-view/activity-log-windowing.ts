import { signal } from '@angular/core';

export class ConversationHistoryWindow {
  readonly size = signal(100);
  private previousCount = 0;
  private previousFirstKey = '';

  slice<T>(items: readonly T[]): readonly T[] {
    return items.slice(Math.max(0, items.length - this.size()));
  }

  olderCount(total: number, visible: number): number {
    return Math.max(0, total - visible);
  }

  sync(firstKey: string, total: number, following: boolean): void {
    if (firstKey !== this.previousFirstKey || total < this.previousCount) {
      this.size.set(100);
    } else if (!following && total > this.previousCount) {
      this.size.update(size => size + total - this.previousCount);
    }
    this.previousFirstKey = firstKey;
    this.previousCount = total;
  }

  loadOlder(remaining: number): void {
    this.size.update(size => size + Math.min(100, remaining));
  }
}
