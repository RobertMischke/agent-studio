import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  HostListener,
  Input,
  OnInit,
  Output,
  ViewEncapsulation,
  computed,
  inject,
  signal,
} from '@angular/core';
import { JobService } from '../services/job.service';
import { CliType, CLI_TYPES, CliModelInfo } from '../models/job.model';
import { cliTypeIcon, cliTypeLabel } from '../services/format.util';
import { HeaderQuotaComponent } from './header-quota';

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
  imports: [HeaderQuotaComponent],
  template: `
    <div class="statusbar" data-testid="status-bar">
      <div class="statusbar__group statusbar__group--left">
        <span class="statusbar__item statusbar__item--ro" [title]="runningTooltip()">
          <span class="statusbar__icon" [class.statusbar__icon--pulse]="runningCount() > 0">●</span>
          <span>{{ runningCount() }} running</span>
        </span>
        <span class="statusbar__item statusbar__item--ro" [title]="autoTooltip()">
          <span class="statusbar__icon">🔁</span>
          <span>{{ autoCount() }}/{{ projectCount() }} auto</span>
        </span>
      </div>

      <!-- The embedded quota cards are tall by default; the
           statusbar__quota overrides shrink them to fit the bar. -->
      <div class="statusbar__group statusbar__group--center statusbar__quota">
        <app-header-quota />
      </div>

      <div class="statusbar__group statusbar__group--right">
        <button class="statusbar__item statusbar__item--btn"
                title="CLI sessions"
                (click)="toggleUsage.emit()">
          <span class="statusbar__icon">🪙</span><span>Usage</span>
        </button>
        <button class="statusbar__item statusbar__item--btn"
                title="Orchestrator chat"
                (click)="toggleOrchestrator.emit()">
          <span class="statusbar__icon">🤖</span><span>Orchestrator</span>
        </button>
        <button class="statusbar__item statusbar__item--btn"
                title="Orchestrator feed"
                (click)="toggleFeed.emit()">
          <span class="statusbar__icon">📜</span><span>Feed</span>
        </button>

        <span class="statusbar__sep" aria-hidden="true"></span>

        <div class="statusbar__pickers">
          <button class="statusbar__item statusbar__item--btn"
                  data-testid="status-bar-cli-picker"
                  [title]="'Default CLI for new tasks: ' + cliLabel(defaultCli())"
                  (click)="toggleCliMenu($event)">
            <span class="statusbar__icon">{{ cliIcon(defaultCli()) }}</span>
            <span>{{ cliLabel(defaultCli()) }}</span>
            <span class="statusbar__caret">▾</span>
          </button>

          @if (cliMenuOpen()) {
            <div class="statusbar__menu" (click)="$event.stopPropagation()">
              <div class="statusbar__menu-title">Default CLI for new tasks</div>
              @for (t of cliTypes; track t) {
                <button class="statusbar__menu-item"
                        [class.statusbar__menu-item--active]="t === defaultCli()"
                        (click)="setDefaultCli(t)">
                  <span class="statusbar__icon">{{ cliIcon(t) }}</span>
                  <span>{{ cliLabel(t) }}</span>
                </button>
              }
            </div>
          }
        </div>

        <div class="statusbar__pickers">
          <button class="statusbar__item statusbar__item--btn"
                  data-testid="status-bar-model-picker"
                  [disabled]="models().length === 0"
                  [title]="'Default model for ' + cliLabel(defaultCli())"
                  (click)="toggleModelMenu($event)">
            <span class="statusbar__icon">⚙</span>
            <span>{{ defaultModelLabel() }}</span>
            <span class="statusbar__caret">▾</span>
          </button>

          @if (modelMenuOpen()) {
            <div class="statusbar__menu" (click)="$event.stopPropagation()">
              <div class="statusbar__menu-title">
                Default model · {{ cliLabel(defaultCli()) }}
              </div>
              @if (models().length === 0) {
                <div class="statusbar__menu-empty">No models reported</div>
              } @else {
                <button class="statusbar__menu-item"
                        [class.statusbar__menu-item--active]="!defaultModel()"
                        (click)="setDefaultModel('')">
                  <span class="statusbar__icon">·</span>
                  <span>CLI default</span>
                </button>
                @for (m of models(); track m.id) {
                  <button class="statusbar__menu-item"
                          [class.statusbar__menu-item--active]="m.id === defaultModel()"
                          [title]="m.id"
                          (click)="setDefaultModel(m.id)">
                    <span class="statusbar__icon">{{ m.isDefault ? '★' : '·' }}</span>
                    <span>{{ m.label || m.id }}</span>
                  </button>
                }
              }
            </div>
          }
        </div>
      </div>
    </div>
  `,
  styles: [`
    :host {
      display: block;
      flex: 0 0 auto;
    }
    .statusbar {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 0 8px;
      height: 30px;
      background: #11111b;
      border-top: 1px solid rgba(255,255,255,0.06);
      font-size: 11px;
      color: rgba(255,255,255,0.70);
      letter-spacing: 0.02em;
    }
    .statusbar__group {
      display: flex;
      align-items: center;
      gap: 4px;
      min-width: 0;
    }
    .statusbar__group--center {
      flex: 1 1 auto;
      justify-content: center;
      overflow: hidden;
    }
    .statusbar__group--right {
      gap: 2px;
    }
    /* Compact the embedded <app-header-quota> so the donut+label cards
       collapse to a single horizontal row that fits the 28px bar.
       Selector chains include .statusbar to outweigh Angular's emulated-
       encapsulation attribute selectors on the inner component's rules. */
    .statusbar .statusbar__quota .hquota__card {
      flex-direction: row;
      gap: 4px;
      padding: 0 8px;
      min-width: 0;
      border: 0;
      background: transparent;
      border-radius: 6px;
    }
    .statusbar .statusbar__quota .hquota__card:hover {
      background: rgba(255,255,255,0.06);
      border: 0;
    }
    .statusbar .statusbar__quota .hquota__head {
      font-size: 10px;
      gap: 3px;
    }
    .statusbar .statusbar__quota .hquota__label {
      font-weight: 500;
      color: rgba(255,255,255,0.55);
    }
    .statusbar .statusbar__quota .hquota__icon { font-size: 10px; }
    .statusbar .statusbar__quota .hquota__svg { width: 18px; height: 18px; }
    .statusbar .statusbar__quota .hquota__svg-text { font-size: 11px; }
    .statusbar .statusbar__quota .hquota__donut { gap: 0; }
    .statusbar .statusbar__quota .hquota__donut-label {
      display: none;
    }
    .statusbar .statusbar__quota .hquota__donuts { gap: 4px; }
    .statusbar .statusbar__quota .hquota__pop {
      bottom: calc(100% + 6px);
      top: auto;
    }
    .statusbar__item {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      padding: 3px 7px;
      border-radius: 4px;
      background: transparent;
      border: 0;
      color: inherit;
      font: inherit;
      line-height: 1;
      white-space: nowrap;
    }
    .statusbar__item--ro {
      color: rgba(255,255,255,0.55);
    }
    .statusbar__item--btn {
      cursor: pointer;
      transition: background 0.12s ease, color 0.12s ease;
    }
    .statusbar__item--btn:hover {
      background: rgba(255,255,255,0.10);
      color: #f8fafc;
    }
    .statusbar__item--btn:disabled {
      opacity: 0.5;
      cursor: not-allowed;
    }
    .statusbar__icon {
      font-size: 11px;
      line-height: 1;
    }
    .statusbar__icon--pulse {
      color: #4ade80;
      animation: statusbar-pulse 1.4s infinite;
    }
    @keyframes statusbar-pulse {
      0%, 100% { opacity: 1; }
      50% { opacity: 0.45; }
    }
    .statusbar__caret {
      font-size: 8px;
      opacity: 0.7;
      margin-left: 1px;
    }
    .statusbar__sep {
      width: 1px;
      height: 16px;
      background: rgba(255,255,255,0.10);
      margin: 0 4px;
    }
    .statusbar__pickers {
      position: relative;
    }
    .statusbar__menu {
      position: absolute;
      bottom: calc(100% + 6px);
      right: 0;
      min-width: 220px;
      max-height: 60vh;
      overflow-y: auto;
      background: #1e1e2e;
      border: 1px solid rgba(255,255,255,0.14);
      border-radius: 10px;
      box-shadow: 0 -12px 40px rgba(0,0,0,0.55);
      padding: 6px;
      z-index: 80;
    }
    .statusbar__menu-title {
      font-size: 10px;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: rgba(255,255,255,0.50);
      padding: 4px 8px 6px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
      margin-bottom: 4px;
    }
    .statusbar__menu-empty {
      padding: 8px;
      color: rgba(255,255,255,0.55);
      font-size: 12px;
    }
    .statusbar__menu-item {
      display: flex;
      align-items: center;
      gap: 8px;
      width: 100%;
      padding: 6px 8px;
      background: transparent;
      border: 0;
      border-radius: 6px;
      color: #cdd6f4;
      font-size: 12px;
      cursor: pointer;
      text-align: left;
    }
    .statusbar__menu-item:hover {
      background: rgba(255,255,255,0.08);
    }
    .statusbar__menu-item--active {
      background: rgba(99,102,241,0.22);
      color: #ffffff;
    }
  `],
})
export class StatusBarComponent implements OnInit {
  private readonly jobService = inject(JobService);

  @Input() projectNames: string[] = [];

  @Output() readonly toggleUsage = new EventEmitter<void>();
  @Output() readonly toggleOrchestrator = new EventEmitter<void>();
  @Output() readonly toggleFeed = new EventEmitter<void>();
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

  @HostListener('document:keydown.escape')
  onEscape() {
    this.cliMenuOpen.set(false);
    this.modelMenuOpen.set(false);
  }

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
