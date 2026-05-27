import { Injectable, computed, signal } from '@angular/core';
import { TagRegistryEntry } from '../models/task.model';

/**
 * Process-wide cache of the workspace tag registry (`GET /api/tags`). The
 * root component refreshes it on init; consumers (cards, filter bar, tag
 * editor) read it as a signal so the UI re-renders the moment a tag is
 * added or removed without an extra round-trip.
 */
@Injectable({ providedIn: 'root' })
export class TagRegistryStore {
  readonly tags = signal<TagRegistryEntry[]>([]);
  readonly byId = computed(() => {
    const map = new Map<string, TagRegistryEntry>();
    for (const t of this.tags()) map.set(t.id, t);
    return map;
  });

  set(entries: TagRegistryEntry[]): void {
    this.tags.set(entries ?? []);
  }
}
