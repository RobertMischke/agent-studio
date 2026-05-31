import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  ViewEncapsulation,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { ClientDefaultsService } from '../../../../services/client-defaults.service';
import type { CliType } from '../../../../models/task.model';
import { CLI_TYPES } from '../../../../models/task.model';
import { UsageHoverPanelComponent } from '../../../tokens';

import { StatusbarItemComponent } from '../statusbar-item/statusbar-item.component';
import { CliModelSelectorComponent } from '../../../../components/cli-model-selector';

const STORAGE_DEFAULT_CLI = 'defaultCliType';
const STORAGE_DEFAULT_MODEL_PREFIX = 'defaultModel:';

@Component({
  selector: 'app-status-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  imports: [UsageHoverPanelComponent, StatusbarItemComponent, CliModelSelectorComponent],
  templateUrl: './status-bar.html',
  styleUrl: './status-bar.scss',
})
export class StatusBarComponent implements OnInit {
  private readonly jobService = inject(TaskService);
  private readonly clientDefaults = inject(ClientDefaultsService);

  readonly projectNames = input<string[]>([]);

  readonly toggleUsage = output<void>();
  readonly toggleOrchestrator = output<void>();
  readonly toggleFeed = output<void>();
  readonly toggleSummary = output<void>();
  readonly toggleVisualEvidence = output<void>();
  readonly toggleCliAdmin = output<void>();
  readonly defaultCliChange = output<CliType>();
  readonly defaultModelChange = output<{ cliType: CliType; model: string }>();

  readonly defaultCli = signal<CliType>(this.readDefaultCli());
  readonly defaultModel = signal<string>(this.readDefaultModel(this.readDefaultCli()));

  readonly runningCount = computed(() => {
    const status = this.jobService.runnerStatus();
    return Object.values(status.projects).filter(p => !!p.activeJobId).length;
  });

  readonly autoCount = computed(() => {
    const status = this.jobService.runnerStatus();
    return Object.values(status.projects).filter(
      p => p.mode === 'auto-continuous' || p.mode === 'auto-single'
    ).length;
  });

  readonly projectCount = computed(() => this.projectNames().length || Object.keys(this.jobService.runnerStatus().projects).length);

  ngOnInit(): void {
    void this.clientDefaults.hydrate().then(() => {
      const cli = this.readDefaultCli();
      this.defaultCli.set(cli);
      this.defaultModel.set(this.readDefaultModel(cli));
    });
  }

  runningTooltip(): string {
    const n = this.runningCount();
    if (n === 0) return 'No tasks currently running.';
    return `${n} task(s) currently executing across all projects.`;
  }

  autoTooltip(): string {
    return `${this.autoCount()} of ${this.projectCount()} project(s) have auto-pickup enabled.`;
  }

  /** Atomic commit from the unified selector. Persists to localStorage and
   *  to the cross-device ClientDefaults profile; the create-task form
   *  subscribes via `defaultCliChange` / `defaultModelChange`. */
  onDefaultCommit(change: { cliType: CliType; model: string }): void {
    const previousCli = this.defaultCli();
    if (change.cliType !== previousCli) {
      this.defaultCli.set(change.cliType);
      localStorage.setItem(STORAGE_DEFAULT_CLI, change.cliType);
      void this.clientDefaults.pushDefaultCli(change.cliType);
      this.defaultCliChange.emit(change.cliType);
      // When the CLI flips we also need a fresh per-CLI model preference.
      this.defaultModel.set(this.readDefaultModel(change.cliType));
    }
    const model = change.model;
    if (model !== this.defaultModel() || change.cliType !== previousCli) {
      this.defaultModel.set(model);
      if (model) {
        localStorage.setItem(STORAGE_DEFAULT_MODEL_PREFIX + change.cliType, model);
      } else {
        localStorage.removeItem(STORAGE_DEFAULT_MODEL_PREFIX + change.cliType);
      }
      void this.clientDefaults.pushDefaultModel(model);
      this.defaultModelChange.emit({ cliType: change.cliType, model });
    }
  }

  private readDefaultCli(): CliType {
    const stored = localStorage.getItem(STORAGE_DEFAULT_CLI) as CliType | null;
    if (stored && (CLI_TYPES as string[]).includes(stored)) return stored;
    return 'copilot';
  }

  private readDefaultModel(cliType: CliType): string {
    return localStorage.getItem(STORAGE_DEFAULT_MODEL_PREFIX + cliType) ?? '';
  }
}
