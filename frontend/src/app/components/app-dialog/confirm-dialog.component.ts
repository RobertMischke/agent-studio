import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  ViewChild,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { ConfirmDialogService } from '../../services/confirm-dialog.service';

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
  templateUrl: './confirm-dialog.component.html',
})
export class ConfirmDialogComponent {
  private readonly service = inject(ConfirmDialogService);

  readonly state = this.service.active;
  readonly hasOpen = computed(() => this.state() !== null);

  @ViewChild('confirmBtn') private confirmBtnRef?: ElementRef<HTMLButtonElement>;

  /** Tracks the previously-focused element so we can restore focus on close. */
  private previousFocus = signal<HTMLElement | null>(null);

  constructor() {
    effect(() => {
      const open = this.hasOpen();
      if (open) {
        const active = (document.activeElement as HTMLElement | null) ?? null;
        this.previousFocus.set(active);
        queueMicrotask(() => this.confirmBtnRef?.nativeElement.focus());
      } else {
        const prev = this.previousFocus();
        this.previousFocus.set(null);
        if (prev && typeof prev.focus === 'function') {
          try { prev.focus(); } catch { /* element gone */ }
        }
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

  @HostListener('document:keydown.escape', ['$event'])
  onEscape(event: Event): void {
    if (!this.hasOpen()) return;
    event.preventDefault();
    event.stopPropagation();
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
