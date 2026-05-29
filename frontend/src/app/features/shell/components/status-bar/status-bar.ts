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
import { JobService } from '../../../../services/task.service';
import { CliCatalogStore } from '../../../../services/cli-catalog.store';
import { ClientDefaultsService } from '../../../../services/client-defaults.service';
import type { CliType } from '../../../../models/task.model';
import { CLI_TYPES } from '../../../../models/task.model';
import type { CliModelInfo } from '../../../../features/cli';
import { cliTypeIcon, cliTypeLabel } from '../../../../services/format.util';
import { UsageHoverPanelComponent } from '../../../tokens';

import { TooltipDirective } from '../../../../components/tooltip';
import { StatusbarItemComponent } from '../statusbar-item/statusbar-item.component';
import { MenuComponent } from '../../../../components/menu';
import type { MenuItem, MenuItemClickEvent } from '../../../../components/menu';
import {
  buildCliMenuItems,
  buildModelMenuItems,
  cliTypeFromMenuId,
  isRefreshAction,
  modelIdFromMenuId,
} from './status-bar-menu-builders';

const STORAGE_DEFAULT_CLI = 'defaultCliType';
const STORAGE_DEFAULT_MODEL_PREFIX = 'defaultModel:';

@Component({
  selector: 'app-status-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  imports: [UsageHoverPanelComponent, TooltipDirective, StatusbarItemComponent, MenuComponent],
  templateUrl: './status-bar.html',
  styleUrl: './status-bar.scss',
})
export class StatusBarComponent implements OnInit {
  private readonly jobService = inject(JobService);
  private readonly catalogStore = inject(CliCatalogStore);
  private readonly clientDefaults = inject(ClientDefaultsService);

  readonly projectNames = input<string[]>([]);

  readonly toggleUsage = output<void>();
  readonly toggleOrchestrator = output<void>();
  readonly toggleFeed = output<void>();
  readonly toggleVisualEvidence = output<void>();
  readonly toggleCliAdmin = output<void>();
  readonly defaultCliChange = output<CliType>();
  readonly defaultModelChange = output<{ cliType: CliType; model: string }>();

  readonly cliTypes = CLI_TYPES;
  readonly defaultCli = signal<CliType>(this.readDefaultCli());
  readonly defaultModel = signal<string>(this.readDefaultModel(this.readDefaultCli()));
  readonly models = signal<CliModelInfo[]>([]);
  readonly modelsLoading = signal(false);
  readonly modelsError = signal(false);

  readonly cliMenuOpen = signal(false);
  readonly modelMenuOpen = signal(false);

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

  readonly defaultModelLabel = computed(() => {
    const id = this.defaultModel();
    if (!id) return 'CLI default';
    const m = this.models().find(x => x.id === id);
    if (m) return m.label || m.id;
    return id;
  });

  readonly modelPickerTooltip = computed(() => {
    const cli = this.cliLabel(this.defaultCli());
    if (this.modelsLoading()) return `Default model for ${cli} (loading catalog…)`;
    if (this.modelsError()) return `Default model for ${cli} (catalog unavailable — click to refresh)`;
    return `Default model for ${cli}`;
  });

  readonly cliMenuItems = computed<readonly MenuItem[]>(() =>
    buildCliMenuItems({
      cliTypes: this.cliTypes,
      defaultCli: this.defaultCli(),
      cliLabel: cliTypeLabel,
    }),
  );

  readonly modelMenuItems = computed<readonly MenuItem[]>(() =>
    buildModelMenuItems({
      defaultCli: this.defaultCli(),
      defaultModel: this.defaultModel(),
      models: this.models(),
      modelsLoading: this.modelsLoading(),
      modelsError: this.modelsError(),
      cliLabel: cliTypeLabel,
    }),
  );

  ngOnInit(): void {
    this.loadModels(this.defaultCli());
    void this.clientDefaults.hydrate().then(() => {
      const cli = this.readDefaultCli();
      this.defaultCli.set(cli);
      this.defaultModel.set(this.readDefaultModel(cli));
      this.loadModels(cli);
    });
  }

  cliIcon(t: CliType): string { return cliTypeIcon(t); }
  cliLabel(t: CliType): string { return cliTypeLabel(t); }

  runningTooltip(): string {
    const n = this.runningCount();
    if (n === 0) return 'No tasks currently running.';
    return `${n} task(s) currently executing across all projects.`;
  }

  autoTooltip(): string {
    return `${this.autoCount()} of ${this.projectCount()} project(s) have auto-pickup enabled.`;
  }

  toggleCliMenu() {
    this.modelMenuOpen.set(false);
    this.cliMenuOpen.update(v => !v);
  }

  toggleModelMenu() {
    this.cliMenuOpen.set(false);
    this.modelMenuOpen.update(v => !v);
  }

  onCliMenuItemClick(ev: MenuItemClickEvent): void {
    const t = cliTypeFromMenuId(ev.id);
    if (!t) return;
    this.defaultCli.set(t);
    localStorage.setItem(STORAGE_DEFAULT_CLI, t);
    this.defaultModel.set(this.readDefaultModel(t));
    this.loadModels(t);
    this.defaultCliChange.emit(t);
    void this.clientDefaults.pushDefaultCli(t);
  }

  onModelMenuItemClick(ev: MenuItemClickEvent): void {
    if (isRefreshAction(ev.id)) {
      this.loadModels(this.defaultCli(), true);
      return;
    }
    const modelId = modelIdFromMenuId(ev.id);
    if (modelId === null) return;
    const cli = this.defaultCli();
    this.defaultModel.set(modelId);
    if (modelId) {
      localStorage.setItem(STORAGE_DEFAULT_MODEL_PREFIX + cli, modelId);
    } else {
      localStorage.removeItem(STORAGE_DEFAULT_MODEL_PREFIX + cli);
    }
    this.defaultModelChange.emit({ cliType: cli, model: modelId });
    void this.clientDefaults.pushDefaultModel(modelId);
  }

  private loadModels(cliType: CliType, refresh = false) {
    // ADR-0046: read through the process-wide CliCatalogStore so
    // re-opening the status-bar model picker is a synchronous render
    // after the first hydration.
    if (!refresh && this.catalogStore.hasFresh(cliType)) {
      this.models.set([...this.catalogStore.modelsFor(cliType)]);
      this.modelsLoading.set(false);
      this.modelsError.set(false);
      return;
    }
    this.modelsLoading.set(true);
    this.modelsError.set(false);
    const source$ = refresh
      ? this.catalogStore.refresh(cliType)
      : this.catalogStore.ensure(cliType);
    source$.subscribe({
      next: (models) => {
        this.models.set([...models]);
        this.modelsLoading.set(false);
      },
      error: () => {
        this.models.set([]);
        this.modelsError.set(true);
        this.modelsLoading.set(false);
      },
    });
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
