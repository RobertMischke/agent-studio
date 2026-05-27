/**
 * Board feature public API. Cycle 9h / ADR-0034: anything outside
 * `features/board/` should import from this barrel, not from internal
 * files. Enforced softly by the `no-restricted-imports` ESLint rule.
 */

// state
export { BoardFiltersService, type ActiveFilterPill } from './state/board-filters.service';
export { LaneCollapseService } from './state/lane-collapse.service';
export { CreateJobFormService } from './state/create-task-form.service';
export { BoardMutationsService } from './state/board-mutations.service';

// components
export { BoardSearchIconComponent } from './components/board-search-icon/board-search-icon.component';
export { CreateJobDialogComponent, type PendingAttachment } from './components/create-task-dialog/create-task-dialog.component';
export { FiltersDropdownComponent, type TypeFilterOption } from './components/filters-dropdown/filters-dropdown.component';
export { KanbanFilterSidesheetComponent } from './components/kanban-filter-sidesheet/kanban-filter-sidesheet.component';
export { JobCardComponent } from './components/task-card/task-card.component';
export { JobColumnComponent } from './components/task-column/task-column';
export {
  ProjectTabsComponent,
  type ProjectAutoInfo,
  type ProjectTokenChipInfo,
  type ProjectRunnerIndicator,
} from './components/project-tabs/project-tabs.component';
export {
  buildProjectTokenChip,
  projectAutoInfo,
  projectRunnerIndicator,
} from './components/project-tabs/project-chip-view-model';

// utilities
export { splitReadyByPhase } from './components/ready-lane-split.util';
export { groupReviewJobs } from './components/review-grouping.util';
