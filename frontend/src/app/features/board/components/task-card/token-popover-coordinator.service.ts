import { Injectable, effect, inject } from '@angular/core';
import { TaskService } from '../../../../services/task.service';

interface ActiveTokenPopover {
  readonly owner: object;
  readonly close: () => void;
}

/**
 * Owns the one token-usage popover allowed on the board.
 *
 * Cards are tracked by stable task identity, so Angular can reuse a card
 * component when a SignalR update replaces the board snapshot. Observing the
 * grouped snapshot here ensures an overlay never survives that data boundary,
 * even when its anchor component instance is reused.
 */
@Injectable({ providedIn: 'root' })
export class TokenPopoverCoordinator {
  private readonly tasks = inject(TaskService);
  private active: ActiveTokenPopover | null = null;
  private hasSeenBoardSnapshot = false;

  private readonly closeOnBoardRefresh = effect(() => {
    this.tasks.grouped();
    if (this.hasSeenBoardSnapshot) {
      this.dismissActive();
    } else {
      this.hasSeenBoardSnapshot = true;
    }
  });

  claim(owner: object, close: () => void): void {
    if (this.active?.owner === owner) return;

    const previous = this.active;
    this.active = { owner, close };
    previous?.close();
  }

  release(owner: object): void {
    if (this.active?.owner === owner) this.active = null;
  }

  private dismissActive(): void {
    const current = this.active;
    this.active = null;
    current?.close();
  }
}
