import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import type { CliType } from '../../models/task.model';
import { CLI_TYPES } from '../../models/task.model';
import type { CliModelInfo } from '../../features/cli';
import {
  cliTypeIcon as fmtCliTypeIcon,
  cliTypeLabel as fmtCliTypeLabel,
  shortModelName,
} from '../../services/format.util';
import { TooltipDirective } from '../tooltip';
import { ModalStackService } from '../../services/modal-stack.service';
import { CliCatalogStore } from '../../services/cli-catalog.store';

/**
 * Unified CLI + model selector. One reusable chip-with-popover control
 * used in every place the user picks a CLI and/or a model: chat-compose
 * footer, overview "Agent" row, command-deck, create-task dialog,
 * code-review panel, and status-bar default pickers.
 *
 * Behaviour mirrors the historical `chat-model-badge`: clicking the
 * chip opens an inline popover with a row of CLI pills (every entry of
 * `CLI_TYPES`, never filtered) and a column of model pills sourced from
 * `CliCatalogStore`. Selecting a model without first changing the CLI
 * auto-commits. Touching the CLI keeps the popover open until Done so
 * both fields commit atomically.
 *
 * Catalog reads go through `CliCatalogStore` (process-wide, hydrated at
 * boot per ADR-0046). The optional `availableModels` input is only a
 * snapshot fallback for the very first render before hydration lands.
 *
 * Three event surfaces:
 * - `commit({ cliType, model })` - atomic, fires from Done or auto-commit.
 * - `cliTypeChange(cliType)` and `modelChange(model)` - derived, fire
 *   for parents that already split the two PUTs (status-bar default,
 *   command-deck, create-task form).
 */
@Component({
  selector: 'app-cli-model-selector',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective],
  templateUrl: './cli-model-selector.component.html',
  styleUrls: ['./cli-model-selector.component.scss'],
})
export class CliModelSelectorComponent {
  readonly cliType = input<CliType | null>(null);
  readonly model = input<string | null>(null);
  /** Optional snapshot fallback used only before the catalog store has hydrated. */
  readonly availableModels = input<readonly CliModelInfo[]>([]);
  /** True while the surface should suppress the click (e.g. a run is in flight). */
  readonly disabled = input<boolean>(false);
  /**
   * Optional explicit reason shown in the tooltip when `disabled` is true.
   * Falls back to "Stop the run first to change the model." so callers can
   * usually leave it unset.
   */
  readonly disabledReason = input<string | null>(null);
  /** Optional eyebrow shown above the current value in the popover header. */
  readonly eyebrow = input<string>('Configure agent');
  /** Override the default chip aria-label ("Model: cli · model"). */
  readonly ariaLabelOverride = input<string | null>(null);
  /** Override the default chip tooltip. Falls back to the canonical "cli · model" text. */
  readonly tooltipOverride = input<string | null>(null);
  /**
   * Testid for the trigger chip. Existing call-sites pass their legacy
   * value (e.g. "chat-compose-model") so the migration is invisible to
   * e2e specs.
   */
  readonly triggerTestid = input<string>('cli-model-selector-trigger');
  /**
   * Prefix for every testid inside the popover (picker root, cli pills,
   * model pills, footer buttons). Defaults to `cli-model-selector-picker`.
   * Call-sites with existing specs pass `chat-model-picker` to preserve
   * locators.
   */
  readonly pickerTestidPrefix = input<string>('cli-model-selector-picker');

  /** Atomic commit: emitted from Done or from an auto-commit on a model click. */
  readonly commit = output<{ cliType: CliType; model: string }>();
  /** Convenience event that always pairs with `commit` when the CLI changed. */
  readonly cliTypeChange = output<CliType>();
  /** Convenience event that always pairs with `commit` for the new model value. */
  readonly modelChange = output<string>();

  readonly pickerOpen = signal<boolean>(false);

  private readonly modalStack = inject(ModalStackService);
  private readonly catalogStore = inject(CliCatalogStore);
  private readonly destroyRef = inject(DestroyRef);
  private modalStackDispose: (() => void) | null = null;

  /** Draft state initialised when the picker opens. */
  readonly draftCliType = signal<CliType | null>(null);
  readonly draftModel = signal<string>('');
  readonly draftAvailableModels = signal<readonly CliModelInfo[]>([]);
  readonly loadingCatalog = signal<boolean>(false);
  readonly catalogError = signal<string | null>(null);

  readonly cliTypes = CLI_TYPES;

  readonly effectiveDisabledReason = computed<string | null>(() => {
    if (!this.disabled()) return null;
    return this.disabledReason() ?? 'Stop the run first to change the model.';
  });

  readonly displayName = computed<string>(() => shortModelName(this.model()));

  readonly cliEmoji = computed<string>(() => {
    const t = this.cliType();
    return t ? fmtCliTypeIcon(t) : '·';
  });

  /** Canonical "{cli} · {model}" rendering used for the picker header, tooltip, and aria-label. */
  readonly currentBadgeText = computed<string>(() => buildBadgeText(this.cliType(), this.model()));

  readonly draftHeaderText = computed<string>(() => {
    const t = this.draftCliType();
    const m = this.draftModel();
    return buildBadgeText(t, m && m.length > 0 ? m : null);
  });

  readonly tooltip = computed<string>(() => {
    const override = this.tooltipOverride();
    if (override !== null) return override;
    const base = this.currentBadgeText();
    const reason = this.effectiveDisabledReason();
    if (reason) return `${base}\n${reason}`;
    return `${base} - click or right-click to change`;
  });

  readonly ariaLabel = computed<string>(
    () => this.ariaLabelOverride() ?? `Model: ${this.currentBadgeText()}`,
  );

  readonly hasChanges = computed<boolean>(() => {
    if (!this.pickerOpen()) return false;
    const cliChanged = this.draftCliType() !== this.cliType();
    const modelInput = (this.model() ?? '').trim();
    const modelChanged = this.draftModel() !== modelInput;
    return cliChanged || modelChanged;
  });

  @ViewChild('triggerBtn') private triggerBtnRef?: ElementRef<HTMLButtonElement>;

  constructor() {
    effect(() => {
      const open = this.pickerOpen();
      if (open) this.acquireModalStack();
      else this.releaseModalStack();
    });
  }

  openPicker(event: MouseEvent): void {
    if (this.effectiveDisabledReason() !== null) return;
    event.preventDefault();
    event.stopPropagation();
    const currentCli = this.cliType();
    const currentModel = (this.model() ?? '').trim();
    this.draftCliType.set(currentCli);
    this.draftModel.set(currentModel);
    this.catalogError.set(null);
    this.loadingCatalog.set(false);
    const cached = currentCli ? this.catalogStore.modelsFor(currentCli) : [];
    this.draftAvailableModels.set(cached.length > 0 ? cached : this.availableModels());
    this.pickerOpen.set(true);
  }

  closePicker(): void {
    this.pickerOpen.set(false);
    this.releaseModalStack();
    queueMicrotask(() => this.triggerBtnRef?.nativeElement.focus());
  }

  onCliPillClick(t: CliType): void {
    if (t === this.draftCliType() && this.catalogStore.hasFresh(t)) return;
    this.draftCliType.set(t);
    if (this.catalogStore.hasFresh(t)) {
      this.applyCatalog(this.catalogStore.modelsFor(t));
      return;
    }
    this.catalogError.set(null);
    this.loadingCatalog.set(true);
    this.draftAvailableModels.set([]);
    this.draftModel.set('');
    this.catalogStore.ensure(t).subscribe({
      next: (models) => {
        if (this.draftCliType() !== t) return;
        this.loadingCatalog.set(false);
        this.applyCatalog(models);
      },
      error: () => {
        if (this.draftCliType() !== t) return;
        this.loadingCatalog.set(false);
        this.catalogError.set(
          'Could not load the model catalog for this CLI. Pick another CLI or try again.',
        );
        this.draftAvailableModels.set([]);
        this.draftModel.set('');
      },
    });
  }

  onModelPillClick(modelId: string): void {
    this.draftModel.set(modelId);
    if (this.draftCliType() === this.cliType()) {
      this.onDoneClick();
    }
  }

  onDefaultModelClick(): void {
    this.draftModel.set('');
    if (this.draftCliType() === this.cliType()) {
      this.onDoneClick();
    }
  }

  onDoneClick(): void {
    if (!this.pickerOpen()) return;
    const cli = this.draftCliType();
    if (!cli) {
      this.closePicker();
      return;
    }
    if (this.hasChanges()) {
      const change = { cliType: cli, model: this.draftModel() };
      if (cli !== this.cliType()) this.cliTypeChange.emit(cli);
      this.modelChange.emit(change.model);
      this.commit.emit(change);
    }
    this.closePicker();
  }

  onCancelClick(): void {
    this.closePicker();
  }

  onBackdropClick(): void {
    this.closePicker();
  }

  /** Explicit "Refresh catalog" affordance in the popover footer. */
  refreshCurrentCatalog(): void {
    const cli = this.draftCliType();
    if (!cli) return;
    this.catalogError.set(null);
    this.loadingCatalog.set(true);
    this.catalogStore.refresh(cli).subscribe({
      next: (models) => {
        if (this.draftCliType() !== cli) return;
        this.loadingCatalog.set(false);
        this.applyCatalog(models);
      },
      error: () => {
        if (this.draftCliType() !== cli) return;
        this.loadingCatalog.set(false);
        this.catalogError.set(
          'Could not refresh the model catalog. Try again in a moment.',
        );
      },
    });
  }

  private applyCatalog(models: readonly CliModelInfo[]): void {
    this.draftAvailableModels.set(models);
    const current = this.draftModel();
    const stillValid = current.length > 0 && models.some((m) => m.id === current);
    if (stillValid) return;
    const def = models.find((m) => m.isDefault);
    this.draftModel.set(def ? def.id : '');
  }

  private acquireModalStack(): void {
    if (this.modalStackDispose !== null) return;
    this.modalStackDispose = this.modalStack.pushUntilDestroyed(
      'cli-model-selector-picker',
      () => {
        this.closePicker();
        return true;
      },
      this.destroyRef,
    );
  }

  private releaseModalStack(): void {
    if (this.modalStackDispose === null) return;
    this.modalStackDispose();
    this.modalStackDispose = null;
  }

  cliTypeLabel(t: CliType): string {
    return fmtCliTypeLabel(t);
  }

  cliTypeIcon(t: CliType): string {
    return fmtCliTypeIcon(t);
  }
}

function buildBadgeText(cliType: CliType | null, model: string | null): string {
  const cli = cliType ? fmtCliTypeLabel(cliType) : 'no CLI';
  const mTrim = model && model.trim() ? model.trim() : 'CLI default';
  return `${cli} · ${mTrim}`;
}
