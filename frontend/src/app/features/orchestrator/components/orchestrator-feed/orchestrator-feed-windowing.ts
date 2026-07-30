import { signal } from '@angular/core';

const PAGE_SIZE = 100;

/**
 * Bounded variable-height history window. This deliberately follows the
 * Activity conversation fix instead of FixedSize virtual scroll: summaries,
 * token rows, and responsive wrapping do not have one reliable row height.
 */
export class OrchestratorFeedWindow {
  readonly size = signal(PAGE_SIZE);
  private previousScope = '';
  private previousCount = 0;

  slice<T>(items: readonly T[]): readonly T[] {
    return items.slice(0, this.size());
  }

  remaining(total: number, visible: number): number {
    return Math.max(0, total - visible);
  }

  sync(scope: string, total: number, followingNewest: boolean): number {
    const grewBy = scope === this.previousScope ? Math.max(0, total - this.previousCount) : 0;
    if (scope !== this.previousScope || total < this.previousCount) {
      this.size.set(PAGE_SIZE);
    } else if (!followingNewest && grewBy > 0) {
      this.size.update(size => size + grewBy);
    }
    this.previousScope = scope;
    this.previousCount = total;
    return grewBy;
  }

  reset(scope: string): void {
    this.previousScope = scope;
    this.previousCount = 0;
    this.size.set(PAGE_SIZE);
  }

  loadOlder(remaining: number): void {
    this.size.update(size => size + Math.min(PAGE_SIZE, remaining));
  }
}
