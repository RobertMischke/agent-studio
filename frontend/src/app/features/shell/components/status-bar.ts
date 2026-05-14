import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  EventEmitter,
  HostListener,
  Input,
  OnInit,
  Output,
  ViewEncapsulation,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { JobService } from '../../../services/job.service';
import { ModalStackService } from '../../../services/modal-stack.service';
import type { CliType } from '../../../models/job.model';
import { CLI_TYPES } from '../../../models/job.model';
import type { CliModelInfo } from '../../../features/cli';
import { cliTypeIcon, cliTypeLabel } from '../../../services/format.util';
import { UsageHoverPanelComponent } from '../../tokens/components/usage-hover-panel';

const STORAGE_DEFAULT_CLI = 'defaultCliType';
const STORAGE_DEFAULT_MODEL_PREFIX = 'defaultModel:';

/**
 * VS Code-style status bar pinned to the bottom of the app shell. Carries
 * compact quota indicators, the current "default CLI / model" used when
 * creating new tasks, and quick toggles for the secondary side sheets
 * (CLI Usage, Orchestrator chat, Orchestrator feed).
 *
 * The bar persists the default CLI + per-CLI default model in localStorage
 * so the same picks survive a reload, and emits changes upward so the
 * shell can pre-fill the create-task dialog with them.
 */
@Component({
  selector: 'app-status-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  // Drop view encapsulation so the .statusbar__quota overrides reach the
  // inner <app-header-quota> classes (.hquota__card, .hquota__svg, ...).
  // Selectors stay scoped via the .statusbar__quota class so we don't
  // leak globally to other usages of header-quota.
  encapsulation: ViewEncapsulation.None,
  imports: [UsageHoverPanelComponent],
  templateUrl: './status-bar.html',
  styleUrl: './status-bar.scss',
})
export class StatusBarComponent implements OnInit {
  private readonly jobService = inject(JobService);

  @Input() projectNames: string[] = [];

  @Output() readonly toggleUsage = new EventEmitter<void>();
  @Output() readonly toggleOrchestrator = new EventEmitter<void>();
  @Output() readonly toggleFeed = new EventEmitter<void>();
  @Output() readonly toggleVisualEvidence = new EventEmitter<void>();
  @Output() readonly toggleCliAdmin = new EventEmitter<void>();
  @Output() readonly defaultCliChange = new EventEmitter<CliType>();
  @Output() readonly defaultModelChange = new EventEmitter<{ cliType: CliType; model: string }>();

  readonly cliTypes = CLI_TYPES;
  readonly defaultCli = signal<CliType>(this.readDefaultCli());
  readonly defaultModel = signal<string>(this.readDefaultModel(this.readDefaultCli()));
  readonly models = signal<CliModelInfo[]>([]);

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

  readonly projectCount = computed(() => this.projectNames.length || Object.keys(this.jobService.runnerStatus().projects).length);

  readonly defaultModelLabel = computed(() => {
    const id = this.defaultModel();
    if (!id) return 'CLI default';
    const m = this.models().find(x => x.id === id);
    if (m) return m.label || m.id;
    return id;
  });

  ngOnInit(): void {
    this.loadModels(this.defaultCli());
  }

  // Status-bar dropdowns register on the modal stack while open so they
  // win Escape over the detail view below, and so a real modal above
  // (Add Task, confirm-dialog) wins Escape over them.
  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);
  private cliMenuDispose: (() => void) | null = null;
  private modelMenuDispose: (() => void) | null = null;
  private readonly cliMenuEffect = effect(() => {
    const open = this.cliMenuOpen();
    if (open && !this.cliMenuDispose) {
      this.cliMenuDispose = this.modalStack.push('status-bar-cli-menu', () => this.cliMenuOpen.set(false));
    } else if (!open && this.cliMenuDispose) {
      this.cliMenuDispose();
      this.cliMenuDispose = null;
    }
  });
  private readonly modelMenuEffect = effect(() => {
    const open = this.modelMenuOpen();
    if (open && !this.modelMenuDispose) {
      this.modelMenuDispose = this.modalStack.push('status-bar-model-menu', () => this.modelMenuOpen.set(false));
    } else if (!open && this.modelMenuDispose) {
      this.modelMenuDispose();
      this.modelMenuDispose = null;
    }
  });
  // Destroy hook is set up via DestroyRef so we drop the entry even if the
  // host node is torn down with the dropdown still open (e.g. router nav).
  private readonly statusBarTeardown = this.destroyRef.onDestroy(() => {
    this.cliMenuDispose?.();
    this.modelMenuDispose?.();
  });

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

  toggleCliMenu(ev: MouseEvent) {
    ev.stopPropagation();
    this.modelMenuOpen.set(false);
    this.cliMenuOpen.update(v => !v);
  }

  toggleModelMenu(ev: MouseEvent) {
    ev.stopPropagation();
    this.cliMenuOpen.set(false);
    this.modelMenuOpen.update(v => !v);
  }

  setDefaultCli(t: CliType) {
    this.defaultCli.set(t);
    localStorage.setItem(STORAGE_DEFAULT_CLI, t);
    this.cliMenuOpen.set(false);
    this.defaultModel.set(this.readDefaultModel(t));
    this.loadModels(t);
    this.defaultCliChange.emit(t);
  }

  setDefaultModel(modelId: string) {
    const cli = this.defaultCli();
    this.defaultModel.set(modelId);
    if (modelId) {
      localStorage.setItem(STORAGE_DEFAULT_MODEL_PREFIX + cli, modelId);
    } else {
      localStorage.removeItem(STORAGE_DEFAULT_MODEL_PREFIX + cli);
    }
    this.modelMenuOpen.set(false);
    this.defaultModelChange.emit({ cliType: cli, model: modelId });
  }

  @HostListener('document:click')
  onDocumentClick() {
    this.cliMenuOpen.set(false);
    this.modelMenuOpen.set(false);
  }

  // Escape handling is delegated to ModalStackService (effects above register
  // an entry per open dropdown). The previous local @HostListener was removed.

  private loadModels(cliType: CliType) {
    this.jobService.getCliModelCatalog(cliType).subscribe({
      next: (catalog) => this.models.set(catalog.models ?? []),
      error: () => this.models.set([]),
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
