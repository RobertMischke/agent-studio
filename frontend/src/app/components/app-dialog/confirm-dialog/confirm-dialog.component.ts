import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  ViewChild,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { ConfirmDialogService } from '../../../services/confirm-dialog.service';
import { ModalStackService } from '../../../services/modal-stack.service';
import { DialogComponent } from '../../dialog/dialog.component';

/**
 * App-wide confirm dialog. Mounted once at the shell; visibility driven by
 * `ConfirmDialogService.active()`. Renders a modal with the same panel,
 * eyebrow, typography, and footer pattern as the error overlay so every
 * confirmation feels native to the dark Catppuccin-inspired UI.
 *
 * Behaviour:
 *  - Esc cancels.
 *  - Enter confirms (primary action).
 *  - Backdrop click cancels.
 *  - Focus is moved to the primary button when the dialog opens and the
 *    Tab key is trapped between the two action buttons + close affordance.
 *
 * Errors with stack-trace / copy remain on `ErrorDialogService` — those
 * carry strictly more information and rely on the dedicated overlay.
 */
@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DialogComponent],
  templateUrl: './confirm-dialog.component.html',
})
export class ConfirmDialogComponent {
  private readonly service = inject(ConfirmDialogService);
  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);

  readonly state = this.service.active;
  readonly hasOpen = computed(() => this.state() !== null);

  @ViewChild('confirmBtn') private confirmBtnRef?: ElementRef<HTMLButtonElement>;

  /** Tracks the previously-focused element so we can restore focus on close. */
  private previousFocus = signal<HTMLElement | null>(null);
  private modalStackDispose: (() => void) | null = null;

  constructor() {
    effect(() => {
      const open = this.hasOpen();
      if (open) {
        const active = (document.activeElement as HTMLElement | null) ?? null;
        this.previousFocus.set(active);
        queueMicrotask(() => this.confirmBtnRef?.nativeElement.focus());
        if (!this.modalStackDispose) {
          this.modalStackDispose = this.modalStack.push('confirm-dialog', () => this.service.cancel());
        }
      } else {
        const prev = this.previousFocus();
        this.previousFocus.set(null);
        if (prev && typeof prev.focus === 'function') {
          try { prev.focus(); } catch { /* element gone */ }
        }
        if (this.modalStackDispose) {
          this.modalStackDispose();
          this.modalStackDispose = null;
        }
      }
    });
    this.destroyRef.onDestroy(() => {
      if (this.modalStackDispose) {
        this.modalStackDispose();
        this.modalStackDispose = null;
      }
    });
  }

  onAccept(): void {
    this.service.accept();
  }

  onCancel(): void {
    this.service.cancel();
  }

  onBackdropClick(): void {
    this.service.cancel();
  }

  /**
   * Enter confirms when the focus is on the dialog. We avoid hijacking
   * Enter globally — the global handler is scoped to keydown originating
   * inside the dialog's panel via the template.
   */
  onPanelKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Enter') return;
    const target = event.target as HTMLElement | null;
    if (target?.tagName === 'TEXTAREA') return;
    event.preventDefault();
    event.stopPropagation();
    this.service.accept();
  }
}
