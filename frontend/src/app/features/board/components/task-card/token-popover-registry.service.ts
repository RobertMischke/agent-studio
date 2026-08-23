import { Injectable, effect, inject } from '@angular/core';
import { TaskService } from '../../../../services/task.service';

export interface TokenPopoverHandle {
  close(): void;
}

/**
 * Board-wide exclusivity for the token-usage popover (AGT-2656 / AGT-2675).
 *
 * Every card's `TokenPopoverDirective` registers here on open. `open()`
 * closes whatever was previously active before taking over, so hovering a
 * second card always dismisses the first — hover across many cards can
 * never leave a trail of stacked panels open at once.
 *
 * Also closes the active popover on `TaskService.boardRefreshedAt`: a
 * wholesale board re-fetch (initial load, manual refresh, or a
 * SignalR-coalesced silent poll) can reorder/replace the underlying job
 * data the panel is showing, so a popover must not survive it. This is
 * deliberately keyed off `boardRefreshedAt` rather than `jobs()`/`grouped()`
 * identity, which also change on granular single-task pushes — closing on
 * every one of those would dismiss the panel out from under a user
 * inspecting a task that happens to be actively streaming updates.
 */
@Injectable({ providedIn: 'root' })
export class TokenPopoverRegistry {
  private readonly taskService = inject(TaskService);
  private active: TokenPopoverHandle | null = null;

  constructor() {
    effect(() => {
      this.taskService.boardRefreshedAt();
      this.active?.close();
    });
  }

  open(handle: TokenPopoverHandle): void {
    if (this.active && this.active !== handle) this.active.close();
    this.active = handle;
  }

  close(handle: TokenPopoverHandle): void {
    if (this.active === handle) this.active = null;
  }
}
