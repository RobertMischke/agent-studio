import {
  ChangeDetectionStrategy,
  Component,
  input,
  output,
} from '@angular/core';
import type {
  WorkbenchOverviewSortDirection,
  WorkbenchOverviewSortKey,
} from '../workbench-overview/workbench-overview-state';

interface SortOption {
  key: WorkbenchOverviewSortKey;
  label: string;
}

@Component({
  selector: 'app-workbench-overview-controls',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-overview-controls.component.html',
  styleUrl: './workbench-overview-controls.component.scss',
})
export class WorkbenchOverviewControlsComponent {
  readonly query = input('');
  readonly sortKey = input.required<WorkbenchOverviewSortKey>();
  readonly direction = input.required<WorkbenchOverviewSortDirection>();
  readonly visibleCount = input.required<number>();
  readonly totalCount = input.required<number>();
  readonly queryChange = output<string>();
  readonly sortSelected = output<WorkbenchOverviewSortKey>();

  readonly sortOptions: readonly SortOption[] = [
    { key: 'default', label: 'Default' },
    { key: 'status', label: 'Status' },
    { key: 'updated', label: 'Last movement' },
    { key: 'project', label: 'Project' },
    { key: 'key', label: 'Key' },
    { key: 'decisions', label: 'Open decisions' },
  ];

  inputValue(event: Event): string {
    return (event.target as HTMLInputElement | null)?.value ?? '';
  }

  sortAriaLabel(option: SortOption): string {
    if (this.sortKey() !== option.key || option.key === 'default') return `Sort by ${option.label}`;
    return `Sort by ${option.label}, ${this.direction() === 'asc' ? 'ascending' : 'descending'}`;
  }
}
