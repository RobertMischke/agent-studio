import type { StepKind } from './models/task-pipeline.model';

/** Canonical human label for a pipeline step kind. Single source of truth
 *  so every surface (Overview pane, task-card token popover, ...) uses the
 *  same words for the same {@link StepKind}. */
export function stepKindLabel(kind: StepKind): string {
  switch (kind) {
    case 'module':       return 'Pre steps';
    case 'core':         return 'Core agent work';
    case 'aspect':       return 'Aspect';
    case 'orchestrator': return 'Decision';
    case 'tool':         return 'Tool';
    case 'analysis':     return 'Analysis';
    case 'drift':        return 'Drift';
    default:             return kind;
  }
}
