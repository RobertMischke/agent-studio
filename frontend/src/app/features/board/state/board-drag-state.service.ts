import { Injectable, signal } from '@angular/core';

/**
 * Shared card-drag state for board-level drop targets that are normally
 * absent from the DOM. A source column starts the gesture; every board
 * consumer can then expose its transient targets until drop or drag-end.
 */
@Injectable({ providedIn: 'root' })
export class BoardDragStateService {
  readonly active = signal(false);

  start(): void {
    this.active.set(true);
  }

  end(): void {
    this.active.set(false);
  }
}
