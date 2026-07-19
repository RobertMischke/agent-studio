import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import {
  managementActionLabel,
  type ManagementActionKind,
  type ManagementActionResult,
} from '../../models/task-server.model';

/** One management sweep offered as a button. */
interface ManagementAction {
  kind: ManagementActionKind;
  label: string;
  description: string;
  /** Danger styling for destructive sweeps. */
  danger: boolean;
}

/**
 * Management functions for the Task-Server page (AGT-1924): Archive sweep,
 * Orphan scan, and Fixture cleanup. Presentational - it takes the busy state +
 * recent results and emits a `run` event; the panel owns the service call.
 *
 * Each sweep's outcome renders quietly as a settled history row (R4): a past
 * sweep is not acute, so it uses a neutral tint, a "found 0" run reads calm,
 * and only the in-flight button carries a live "Running…" affordance.
 */
@Component({
  selector: 'app-task-server-management-panel',
  standalone: true,
  templateUrl: './task-server-management-panel.html',
  styleUrl: './task-server-management-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskServerManagementPanelComponent {
  /** Recent sweep outcomes, newest first. */
  readonly results = input<readonly ManagementActionResult[]>([]);
  /** The sweep currently running, or null when idle. */
  readonly busyAction = input<ManagementActionKind | null>(null);
  readonly run = output<ManagementActionKind>();

  readonly actions: readonly ManagementAction[] = [
    { kind: 'archive-sweep', label: 'Archive sweep', description: 'Move settled tasks past the retention threshold into the archive.', danger: false },
    { kind: 'orphan-scan', label: 'Orphan scan', description: 'Find worktrees and job folders with no owning task.', danger: false },
    { kind: 'fixture-cleanup', label: 'Fixture cleanup', description: 'Remove leftover e2e fixture tasks and scratch data.', danger: true },
  ];

  actionLabel(kind: ManagementActionKind): string { return managementActionLabel(kind); }

  emit(kind: ManagementActionKind): void {
    if (this.busyAction()) return;
    this.run.emit(kind);
  }
}
