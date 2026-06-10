import { Injectable, signal } from '@angular/core';

/**
 * Board feature service holding which epic cards have their inline sub-task
 * list expanded, keyed by the epic's task id.
 *
 * Why a service and not a per-card signal: the inline expand state used to
 * live on `TaskCardComponent` as a local signal. That survives an ordinary
 * poll (the lane `@for` tracks on `taskKey`, so the instance is reused) but
 * is lost the moment the card is re-mounted - the epic changing lane, the
 * group-by-epic toggle swapping the board, or a filter rebuild. To the
 * operator that read as the expand "jumping" shut on its own. Lifting the
 * state into a singleton keyed by id makes it independent of the card's DOM
 * lifecycle: the same epic re-rendered in a fresh component instance reads
 * back its expanded state, so a refresh no longer collapses it.
 *
 * In-memory only (no localStorage): the contract is stability across polling
 * cycles within a session, not persistence across reloads.
 */
@Injectable({ providedIn: 'root' })
export class EpicExpansionStore {
  private readonly expandedIds = signal<ReadonlySet<string>>(new Set());

  isExpanded(epicId: string): boolean {
    return this.expandedIds().has(epicId);
  }

  toggle(epicId: string): void {
    const next = new Set(this.expandedIds());
    if (next.has(epicId)) next.delete(epicId);
    else next.add(epicId);
    this.expandedIds.set(next);
  }

  setExpanded(epicId: string, expanded: boolean): void {
    if (this.isExpanded(epicId) === expanded) return;
    const next = new Set(this.expandedIds());
    if (expanded) next.add(epicId);
    else next.delete(epicId);
    this.expandedIds.set(next);
  }
}
