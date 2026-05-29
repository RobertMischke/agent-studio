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
import type { CliType } from '../../../../models/task.model';
import { CLI_TYPES } from '../../../../models/task.model';
import type { CliModelInfo } from '../../../cli';
import {
  cliTypeIcon as fmtCliTypeIcon,
  cliTypeLabel as fmtCliTypeLabel,
} from '../../../../services/format.util';
import {
  currentBadgeText,
  modelBadgeTooltip,
  shortModelName,
} from '../protocol-pane/protocol-pane/model-badge-menu-builders';
import { TooltipDirective } from '../../../../components/tooltip';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { CliCatalogStore } from '../../../../services/cli-catalog.store';

/**
 * Subtle CLI + model badge sitting next to the chat composer. Click /
 * right-click opens an atomic configure-agent picker: the user can switch
 * CLI (the model list refreshes live without closing the dialog) and pick
 * a model in one interaction. Nothing is persisted until Done is clicked;
 * Esc / outside-click / Cancel reverts the draft without firing a PUT.
 *
 * Catalog fetches go through `CliCatalogStore` (process-wide cache,
 * hydrated at app boot). The badge therefore opens without a network
 * round-trip in steady state — see ADR-0046.
 *
 * Emits a single `commit` event with `{cliType, model}` so the parent can
 * make the two-field change atomic — empty-string `model` means
 * "fall back to the CLI default", matching the legacy convention.
 */
@Component({
  selector: 'app-chat-model-badge',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective],
  templateUrl: './chat-model-badge.component.html',
  styleUrls: ['./chat-model-badge.component.scss'],
})
export class ChatModelBadgeComponent {
  readonly cliType = input<CliType | null>(null);
  readonly model = input<string | null>(null);
  readonly availableModels = input<readonly CliModelInfo[]>([]);
  /** True while a run is in flight; click is suppressed, tooltip explains why. */
  readonly disabled = input<boolean>(false);

  /** Atomic commit: emitted when Done is clicked. The parent issues the
   *  PUT calls in sequence (cli-type, then model). An empty-string `model`
   *  field means "clear back to the CLI default". */
  readonly commit = output<{ cliType: CliType; model: string }>();

  readonly pickerOpen = signal<boolean>(false);

  private readonly modalStack = inject(ModalStackService);
  private readonly catalogStore = inject(CliCatalogStore);
  private readonly destroyRef = inject(DestroyRef);
  private modalStackDispose: (() => void) | null = null;

  /** Draft state — initialised when the picker opens. */
  readonly draftCliType = signal<CliType | null>(null);
  readonly draftModel = signal<string>('');
  /** UI catalog list for the currently-drafted CLI. */
  readonly draftAvailableModels = signal<readonly CliModelInfo[]>([]);
  /** True while a catalog fetch is in flight for the just-clicked CLI. */
  readonly loadingCatalog = signal<boolean>(false);
  /** True when the most recent catalog fetch errored out. */
  readonly catalogError = signal<string | null>(null);

  readonly cliTypes = CLI_TYPES;

  readonly disabledReason = computed<string | null>(() =>
    this.disabled() ? 'Stop the run first to change the model.' : null,
  );

  readonly displayName = computed<string>(() => shortModelName(this.model()));

  readonly cliEmoji = computed<string>(() => {
    const t = this.cliType();
    return t ? fmtCliTypeIcon(t) : '·';
  });

  readonly tooltip = computed<string>(() =>
    modelBadgeTooltip(
      { cliType: this.cliType(), model: this.model(), cliTypeLabel: fmtCliTypeLabel },
      this.disabledReason(),
    ),
  );

  readonly ariaLabel = computed<string>(
    () =>
      `Model: ${currentBadgeText({
        cliType: this.cliType(),
        model: this.model(),
        cliTypeLabel: fmtCliTypeLabel,
      })}`,
  );

  /** True when the draft differs from the inputs — drives the Done button enable. */
  readonly hasChanges = computed<boolean>(() => {
    if (!this.pickerOpen()) return false;
    const cliChanged = this.draftCliType() !== this.cliType();
    const modelInput = (this.model() ?? '').trim();
    const modelChanged = this.draftModel() !== modelInput;
    return cliChanged || modelChanged;
  });

  /** Renders the same "{cli} · {model}" line at the top of the picker. */
  readonly draftHeaderText = computed<string>(() => {
    const t = this.draftCliType();
    const m = this.draftModel();
    return currentBadgeText({
      cliType: t,
      model: m && m.length > 0 ? m : null,
      cliTypeLabel: fmtCliTypeLabel,
    });
  });

  @ViewChild('triggerBtn') private triggerBtnRef?: ElementRef<HTMLButtonElement>;

  constructor() {
    // Acquire / release a modal-stack entry so Esc closes the picker first
    // instead of bubbling to the host detail panel.
    effect(() => {
      const open = this.pickerOpen();
      if (open) {
        this.acquireModalStack();
      } else {
        this.releaseModalStack();
      }
    });
  }

  openPicker(event: MouseEvent): void {
    if (this.disabledReason() !== null) return;
    event.preventDefault();
    event.stopPropagation();
    // Snapshot inputs into draft.
    const currentCli = this.cliType();
    const currentModel = (this.model() ?? '').trim();
    this.draftCliType.set(currentCli);
    this.draftModel.set(currentModel);
    this.catalogError.set(null);
    this.loadingCatalog.set(false);
    // Prefer the process-wide cache (ADR-0046) so the picker has a model
    // list to render synchronously. Fall back to the parent's snapshot for
    // the very first open before boot-hydration has completed.
    const cached = currentCli ? this.catalogStore.modelsFor(currentCli) : [];
    this.draftAvailableModels.set(cached.length > 0 ? cached : this.availableModels());
    this.pickerOpen.set(true);
  }

  closePicker(): void {
    this.pickerOpen.set(false);
    this.releaseModalStack();
    // Restore focus to the trigger so keyboard users do not lose their place.
    queueMicrotask(() => this.triggerBtnRef?.nativeElement.focus());
  }

  /** User clicked a CLI pill. Stay open, refresh model list, default-select. */
  onCliPillClick(t: CliType): void {
    if (t === this.draftCliType() && this.catalogStore.hasFresh(t)) return;
    this.draftCliType.set(t);
    // Process-wide cache hit (ADR-0046): apply synchronously, no spinner.
    if (this.catalogStore.hasFresh(t)) {
      this.applyCatalog(this.catalogStore.modelsFor(t));
      return;
    }
    this.catalogError.set(null);
    this.loadingCatalog.set(true);
    // Clear so stale models from the previous CLI do not linger. The
    // spinner row in the template fills the gap.
    this.draftAvailableModels.set([]);
    this.draftModel.set('');
    this.catalogStore.ensure(t).subscribe({
      next: (models) => {
        // Race guard: only apply if the user hasn't already clicked another CLI.
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

  /** User clicked a model pill in the (drafted) catalog. Auto-commits when
   *  the draft CLI still matches the input CLI: in that case the picker is
   *  acting like a one-shot model selector and the operator expectation is
   *  "click model = persist". When the CLI was changed inside the picker
   *  we keep the picker open so the user can commit both fields with one
   *  Done click - that preserves the atomic-after-CLI-switch flow from
   *  ASS-532 (commit f421f2d). */
  onModelPillClick(modelId: string): void {
    this.draftModel.set(modelId);
    if (this.draftCliType() === this.cliType()) {
      this.onDoneClick();
    }
  }

  /** Default ("CLI default") pill click: clears the explicit model selection.
   *  Same auto-commit semantics as {@link onModelPillClick}. */
  onDefaultModelClick(): void {
    this.draftModel.set('');
    if (this.draftCliType() === this.cliType()) {
      this.onDoneClick();
    }
  }

  /** Done button: emit the atomic commit and close. Skips the emit when
   *  nothing actually changed so we never PUT-noop the backend. */
  onDoneClick(): void {
    if (!this.pickerOpen()) return;
    const cli = this.draftCliType();
    if (!cli) {
      this.closePicker();
      return;
    }
    if (this.hasChanges()) {
      this.commit.emit({ cliType: cli, model: this.draftModel() });
    }
    this.closePicker();
  }

  /** Cancel button / Esc / outside click: discard draft and close. */
  onCancelClick(): void {
    this.closePicker();
  }

  onBackdropClick(): void {
    this.closePicker();
  }

  /** Adopt a freshly-fetched (or cached) catalog: pick the default model when
   *  the previously-drafted model is not in the new list. */
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
      'chat-model-badge-picker',
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

  /** Used by the template's *ngFor over CLI pills. */
  cliTypeLabel(t: CliType): string {
    return fmtCliTypeLabel(t);
  }

  /** Used by the template's *ngFor over CLI pills. */
  cliTypeIcon(t: CliType): string {
    return fmtCliTypeIcon(t);
  }
}
