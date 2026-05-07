import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { TagRegistryEntry } from '../../../models/job.model';

export interface TypeFilterOption {
  /** Backend-side value, e.g. `bug`, `user-story`, `chore`. */
  value: string;
  /** Visible chip label, e.g. `Bugs`. */
  label: string;
  icon: string;
  /** CSS modifier suffix (`bug` / `story` / `chore`). */
  kind: string;
}

/**
 * Combined task-type + tag filter dropdown. Replaces the previous inline
 * pill rows in the header so the chrome stays calm; the trigger button
 * shows a count badge of active filter selections (excluding the global
 * Owner / Project filters, which stay inline as their own controls).
 *
 * Type filter is single-select (one type or none); tag filter is
 * multi-select with AND semantics (a job needs all selected tags).
 */
@Component({
  selector: 'app-filters-dropdown',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './filters-dropdown.component.html',
  styleUrls: ['./filters-dropdown.component.scss']
})
export class FiltersDropdownComponent {
  readonly typeOptions = input.required<readonly TypeFilterOption[]>();
  readonly activeType = input<string | null>(null);
  readonly tags = input<readonly TagRegistryEntry[]>([]);
  readonly activeTagIds = input<ReadonlySet<string>>(new Set<string>());

  readonly setType = output<string | null>();
  readonly toggleTag = output<string>();

  readonly open = signal(false);

  readonly badgeCount = computed(() => {
    const types = this.activeType() ? 1 : 0;
    return types + this.activeTagIds().size;
  });

  toggle(): void {
    this.open.update(v => !v);
  }

  close(): void {
    this.open.set(false);
  }

  isTypeActive(value: string): boolean {
    return this.activeType() === value;
  }

  isTagActive(id: string): boolean {
    return this.activeTagIds().has(id);
  }

  pickType(value: string): void {
    if (this.activeType() === value) {
      this.setType.emit(null);
    } else {
      this.setType.emit(value);
    }
  }

  pickAll(): void {
    this.setType.emit(null);
  }

  emitToggleTag(id: string): void {
    this.toggleTag.emit(id);
  }
}
