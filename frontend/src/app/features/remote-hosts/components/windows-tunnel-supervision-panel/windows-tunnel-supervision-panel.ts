import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import { WindowsTunnelSupervisionService } from '../../services/windows-tunnel-supervision.service';
import {
  needsTunnelSupervisionSetup,
  scheduledTaskLabel,
  scheduledTaskTone,
  type ScheduledTaskStatus,
  type ScheduledTaskTone,
} from '../../models/windows-tunnel-supervision.model';

/**
 * Windows control-plane host setup (AGT-2664): status of the tunnel keeper
 * and watchdog Scheduled Tasks the guided install script registers, plus the
 * elevation-aware install command an operator copies and runs locally.
 * Registration itself needs one elevated session and happens outside the
 * browser; this panel only reports what it finds and hands over the command.
 */
@Component({
  selector: 'app-windows-tunnel-supervision-panel',
  standalone: true,
  imports: [FormsModule, DatePipe],
  templateUrl: './windows-tunnel-supervision-panel.html',
  styleUrl: './windows-tunnel-supervision-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WindowsTunnelSupervisionPanelComponent implements OnInit, OnDestroy {
  private readonly service = inject(WindowsTunnelSupervisionService);

  readonly status = this.service.status;
  readonly loading = this.service.loading;
  readonly error = this.service.error;
  readonly expanded = signal(false);
  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');

  readonly sshTarget = signal('agent-runner');
  readonly remotePort = signal(15031);
  readonly taskServerPort = signal(5031);
  readonly devspacePath = signal('C:\\Projects\\agent-taskboard-devspace');

  readonly needsSetup = computed(() => {
    const status = this.status();
    return status !== null && needsTunnelSupervisionSetup(status);
  });

  readonly installCommand = computed(() => [
    '.\\deploy\\windows\\agent-runner-tunnel\\install-tunnel-supervision.ps1',
    `-SshTarget ${this.sshTarget()}`,
    `-RemotePort ${this.remotePort()}`,
    `-TaskServerPort ${this.taskServerPort()}`,
    `-DevspacePath "${this.devspacePath()}"`,
  ].join(' `\n    '));

  ngOnInit(): void {
    this.service.start();
  }

  ngOnDestroy(): void {
    this.service.stop();
  }

  reload(): void {
    this.service.refresh();
  }

  toggleExpanded(): void {
    this.expanded.update(value => !value);
  }

  taskTone(task: ScheduledTaskStatus): ScheduledTaskTone {
    return scheduledTaskTone(task.presence);
  }

  taskLabel(task: ScheduledTaskStatus): string {
    return scheduledTaskLabel(task.presence);
  }

  copyLabel(): string {
    const state = this.copyState();
    return state === 'copied' ? '✓ Copied' : state === 'failed' ? '⚠ Failed' : 'Copy command';
  }

  copyInstallCommand(): void {
    void copyTextToClipboard(this.installCommand()).then(ok => {
      this.copyState.set(ok ? 'copied' : 'failed');
      setTimeout(() => this.copyState.set('idle'), 1600);
    });
  }
}
