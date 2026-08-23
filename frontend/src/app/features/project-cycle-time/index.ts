/** Project cycle-time feature public API (ADR-0034 barrel). */
export { ProjectCycleTimePanelComponent } from './components/project-cycle-time-panel/project-cycle-time-panel.component';
export { ProjectCycleTimeService } from './services/project-cycle-time.service';
export {
  CYCLE_TIME_STAGE_KEYS,
  CYCLE_TIME_WINDOWS,
} from './models/project-cycle-time.model';
export type {
  CycleTimeAggregate,
  CycleTimeAggregateKind,
  CycleTimeOutcomeCount,
  CycleTimeStageKey,
  CycleTimeStageSeconds,
  CycleTimeWindow,
  ProjectCycleTimeCoverage,
  ProjectCycleTimeResponse,
  TaskCycleTimeRow,
} from './models/project-cycle-time.model';
