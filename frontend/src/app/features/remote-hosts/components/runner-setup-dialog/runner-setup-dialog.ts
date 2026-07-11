import { ChangeDetectionStrategy, Component, OnInit, computed, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  VisibleCliTaskCardComponent,
  type VisibleCliTaskCreated,
  type VisibleCliTaskWorkspace,
} from '../../../visible-cli-task';
import type { RemoteHost } from '../../models/remote-host.model';
import {
  buildRunnerSetupRequest,
  runnerSetupIssues,
  type RunnerSetupConfig,
  type RunnerSetupConnectionMode,
} from '../../models/runner-setup.model';

@Component({
  selector: 'app-runner-setup-dialog',
  standalone: true,
  imports: [FormsModule, VisibleCliTaskCardComponent],
  templateUrl: './runner-setup-dialog.html',
  styleUrl: './runner-setup-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunnerSetupDialogComponent implements OnInit {
  readonly host = input.required<RemoteHost>();
  readonly workspaces = input<readonly VisibleCliTaskWorkspace[]>([]);
  readonly cancelled = output<void>();
  readonly taskCreated = output<VisibleCliTaskCreated>();

  readonly sshTarget = signal('');
  readonly taskServerUrl = signal('http://localhost:5031');
  readonly connectionMode = signal<RunnerSetupConnectionMode | ''>('');
  readonly clientId = signal('');
  readonly gitRemote = signal('');

  readonly config = computed<RunnerSetupConfig>(() => ({
    sshTarget: this.sshTarget(),
    taskServerUrl: this.taskServerUrl(),
    connectionMode: this.connectionMode(),
    clientId: this.clientId(),
    gitRemote: this.gitRemote(),
  }));
  readonly issues = computed(() => runnerSetupIssues(this.config()));
  readonly ready = computed(() => this.issues().length === 0);
  readonly request = computed(() => buildRunnerSetupRequest(this.host(), this.config()));
  readonly loopbackBlocked = computed(() => this.issues().some(issue => issue.startsWith('A remote host cannot reach')));

  ngOnInit(): void {
    const host = this.host();
    this.sshTarget.set(host.address ?? '');
    this.clientId.set(host.clientId || host.id);
  }

  setConnectionMode(value: string): void {
    if (value === 'central' || value === 'lan' || value === 'tunnel' || value === '') {
      this.connectionMode.set(value);
      if (value === 'tunnel' && /^http:\/\/(localhost|127\.0\.0\.1):5031\/?$/i.test(this.taskServerUrl().trim())) {
        this.taskServerUrl.set('http://127.0.0.1:15031');
      }
    }
  }

  closeFromBackdrop(event: MouseEvent): void {
    if (event.target === event.currentTarget) this.cancelled.emit();
  }
}
