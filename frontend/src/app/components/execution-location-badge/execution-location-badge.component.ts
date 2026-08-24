import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { REJECTION_CODE_BUILD_PROFILE_GATE, type TaskExecutionLocation } from '../../models/task.model';

@Component({
  selector: 'app-execution-location-badge',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './execution-location-badge.component.html',
  styleUrl: './execution-location-badge.component.scss',
})
export class ExecutionLocationBadgeComponent {
  readonly execution = input<TaskExecutionLocation | null | undefined>(null);
  readonly compact = input(true);
  readonly visible = computed(() => this.execution()?.state !== 'no-active-execution');
  readonly acute = computed(() => this.execution()?.state === 'remote-disconnected' && !this.execution()?.historical);
  readonly label = computed(() => {
    const value = this.execution();
    if (!value) return '';
    if (value.state === 'recovering') return 'Recovering';
    if (value.executionKind === 'local') return 'Local';
    const id = value.runnerId || value.hostDisplayName || value.configuredRunnerId || 'remote';
    return `Host · ${id}`;
  });
  readonly stateLabel = computed(() => ({
    'local-running': 'Local running',
    'remote-running': 'Host running',
    'remote-disconnected': 'Host disconnected or stale',
    'queued-remote': 'Queued for host',
    'recovering': 'Recovering ownership',
    'no-active-execution': 'No active execution',
  }[this.execution()?.state ?? 'no-active-execution']));
  readonly tooltip = computed(() => {
    const value = this.execution();
    if (!value) return '';
    const actual = value.executionKind === 'none'
      ? 'No active host'
      : `${value.executionKind === 'local' ? 'Local' : 'Host'}${value.runnerId ? ` · ${value.runnerId}` : ''}${value.hostDisplayName ? ` (${value.hostDisplayName})` : ''}`;
    const configured = value.configuredRunnerId ? `Host · ${value.configuredRunnerId}` : 'Local';
    const lines = [
      this.stateLabel(),
      `Actual host: ${actual}`,
      `Configured host: ${configured}`,
      value.startedAt ? `Started: ${this.format(value.startedAt)}` : null,
      value.lastHeartbeat ? `Last heartbeat: ${this.format(value.lastHeartbeat)}` : null,
      value.lastActivityAt ? `Last activity: ${this.format(value.lastActivityAt)}` : null,
      value.branch ? `Branch: ${value.branch}` : null,
      value.worktreePath ? `Worktree: ${value.worktreePath}` : null,
      value.processId ? `Process: ${value.processId}` : null,
      value.sessionId ? `Session: ${value.sessionId}` : null,
      value.lastRejection
        ? `Latest rejection: ${value.lastRejection.code === REJECTION_CODE_BUILD_PROFILE_GATE
            ? 'Project build profile not validated'
            : `Runner ${value.lastRejection.runnerName || value.lastRejection.runnerId} rejected`}: ${value.lastRejection.reason}`
        : null,
      `Connection: ${value.connectionState}; lease: ${value.leaseState}`,
      `Trusted because: ${value.trustReason}`,
    ];
    return lines.filter(Boolean).join('\n');
  });

  private format(value: string): string {
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
  }
}
