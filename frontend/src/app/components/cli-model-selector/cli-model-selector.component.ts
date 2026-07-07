import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import type { CliType } from '../../models/task.model';
import { CLI_TYPES } from '../../models/task.model';
import type { CliModelInfo } from '../../features/cli';
import {
  cliTypeIcon as fmtCliTypeIcon,
  cliTypeLabel as fmtCliTypeLabel,
} from '../../services/format.util';
import { ModelSelectorComponent } from 'coding-agent-chat/composer';
import type { ChatCliOption, ChatModelSelection } from 'coding-agent-chat/core';
import { ModalStackService } from '../../services/modal-stack.service';
import { CliCatalogStore } from '../../services/cli-catalog.store';

/**
 * Unified CLI + model selector — thin app adapter around the library's
 * `<cac-model-selector>` (picker UI, draft/commit semantics, keyboard
 * navigation all live in `coding-agent-chat/composer`). This wrapper keeps
 * the historical `app-cli-model-selector` API so the ~10 call-sites and
 * their e2e testids stay untouched, and binds the app-only concerns the
 * library deliberately doesn't know about:
 *
 * - the CLI vocabulary (`CLI_TYPES` + label/icon formatting),
 * - the catalog data source (`CliCatalogStore`, hydrated per ADR-0046):
 *   the library asks via `catalogRequested`/`refreshRequested` and this
 *   adapter answers with `models`/`catalogLoading`/`catalogError`,
 * - the modal stack (Escape/close coordination with app dialogs).
 *
 * Event surfaces are unchanged: `commit({cliType, model, thinkingLevel})`
 * plus the derived `cliTypeChange`/`modelChange`/`thinkingLevelChange`.
 */
@Component({
  selector: 'app-cli-model-selector',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ModelSelectorComponent],
  templateUrl: './cli-model-selector.component.html',
})
export class CliModelSelectorComponent {
  readonly cliType = input<CliType | null>(null);
  readonly model = input<string | null>(null);
  readonly thinkingLevel = input<string | null>(null);
  /** Optional snapshot fallback used only before the catalog store has hydrated. */
  readonly availableModels = input<readonly CliModelInfo[]>([]);
  /** True while the surface should suppress the click (e.g. a run is in flight). */
  readonly disabled = input<boolean>(false);
  /** Optional explicit reason shown in the tooltip when `disabled` is true. */
  readonly disabledReason = input<string | null>(null);
  /** Optional eyebrow shown above the current value in the popover header. */
  readonly eyebrow = input<string>('Configure agent');
  /** Override the default chip aria-label ("Model: cli · model"). */
  readonly ariaLabelOverride = input<string | null>(null);
  /** Override the default chip tooltip. Falls back to the canonical "cli · model" text. */
  readonly tooltipOverride = input<string | null>(null);
  /** Testid for the trigger chip; call-sites pass their legacy value (e.g. "chat-compose-model"). */
  readonly triggerTestid = input<string>('cli-model-selector-trigger');
  /** Prefix for every testid inside the popover; legacy call-sites pass e.g. "chat-model-picker". */
  readonly pickerTestidPrefix = input<string>('cli-model-selector-picker');

  /** Atomic commit: emitted from Done or from an auto-commit on a model click. */
  readonly commit = output<{ cliType: CliType; model: string; thinkingLevel: string | null }>();
  readonly cliTypeChange = output<CliType>();
  readonly modelChange = output<string>();
  readonly thinkingLevelChange = output<string | null>();

  private readonly modalStack = inject(ModalStackService);
  private readonly catalogStore = inject(CliCatalogStore);
  private readonly destroyRef = inject(DestroyRef);
  private modalStackDispose: (() => void) | null = null;

  private readonly selector = viewChild(ModelSelectorComponent);

  readonly cliOptions = computed<readonly ChatCliOption[]>(() =>
    CLI_TYPES.map((t) => ({ id: t, label: fmtCliTypeLabel(t), icon: fmtCliTypeIcon(t) })),
  );

  /** Catalog answer for the library's latest `catalogRequested` CLI. */
  private readonly catalogModels = signal<readonly CliModelInfo[] | null>(null);
  readonly catalogLoading = signal<boolean>(false);
  readonly catalogError = signal<string | null>(null);
  /** Which CLI the in-flight/last catalog answer belongs to — drops stale responses. */
  private lastRequestedCli: CliType | null = null;

  readonly effectiveModels = computed<readonly CliModelInfo[]>(
    () => this.catalogModels() ?? this.availableModels(),
  );

  constructor() {
    // Mirror the library picker's open state onto the app modal stack so
    // Escape closes the picker before any outer dialog.
    effect(() => {
      const open = this.selector()?.pickerOpen() ?? false;
      if (open) {
        this.acquireModalStack();
      } else {
        this.releaseModalStack();
      }
    });
  }

  onCatalogRequested(cli: string): void {
    const t = cli as CliType;
    this.lastRequestedCli = t;
    this.catalogError.set(null);
    if (this.catalogStore.hasFresh(t)) {
      this.catalogLoading.set(false);
      this.catalogModels.set(this.catalogStore.modelsFor(t));
      const refresh = this.catalogStore.refreshForPickerOpen(t);
      refresh?.subscribe({
        next: (models) => {
          if (this.lastRequestedCli !== t) return;
          this.catalogModels.set(models);
        },
        // Keep the synchronous cached catalog visible; explicit Refresh
        // still surfaces errors when the operator asks for it.
        error: () => {},
      });
      return;
    }
    this.catalogLoading.set(true);
    this.catalogModels.set([]);
    this.catalogStore.ensure(t).subscribe({
      next: (models) => {
        if (this.lastRequestedCli !== t) return;
        this.catalogLoading.set(false);
        this.catalogModels.set(models);
      },
      error: () => {
        if (this.lastRequestedCli !== t) return;
        this.catalogLoading.set(false);
        this.catalogModels.set([]);
        this.catalogError.set(
          'Could not load the model catalog for this CLI. Pick another CLI or try again.',
        );
      },
    });
  }

  onRefreshRequested(cli: string): void {
    const t = cli as CliType;
    this.lastRequestedCli = t;
    this.catalogError.set(null);
    this.catalogLoading.set(true);
    this.catalogStore.refresh(t).subscribe({
      next: (models) => {
        if (this.lastRequestedCli !== t) return;
        this.catalogLoading.set(false);
        this.catalogModels.set(models);
      },
      error: () => {
        if (this.lastRequestedCli !== t) return;
        this.catalogLoading.set(false);
        this.catalogError.set('Could not refresh the model catalog. Try again in a moment.');
      },
    });
  }

  onCommit(selection: ChatModelSelection): void {
    this.commit.emit({
      cliType: selection.cliType as CliType,
      model: selection.model,
      thinkingLevel: selection.thinkingLevel,
    });
  }

  onCliTypeChange(cli: string): void {
    this.cliTypeChange.emit(cli as CliType);
  }

  private acquireModalStack(): void {
    if (this.modalStackDispose !== null) return;
    this.modalStackDispose = this.modalStack.pushUntilDestroyed(
      'cli-model-selector-picker',
      () => {
        this.selector()?.closePicker();
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
}
