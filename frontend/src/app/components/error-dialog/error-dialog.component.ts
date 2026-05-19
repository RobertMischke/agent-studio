import { ChangeDetectionStrategy, Component, DestroyRef, inject, input, output } from '@angular/core';
import { ErrorDialogState } from '../../models/error-dialog.model';
import { ModalStackService } from '../../services/modal-stack.service';
import { DialogComponent } from '../dialog/dialog.component';

/**
 * Error overlay used by the global ErrorDialogService. The parent
 * watches `errorDialog.activeError()` and renders this component when
 * an error is present.
 */
@Component({
  selector: 'app-error-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DialogComponent],
  templateUrl: './error-dialog.component.html'
})
export class ErrorDialogComponent {
  readonly error = input.required<ErrorDialogState>();
  readonly canOpenCliConfig = input(false);
  readonly copyButtonLabel = input<string>('Copy details');

  readonly closeRequest = output<void>();
  readonly copyRequest = output<void>();
  readonly openCliConfig = output<void>();

  constructor() {
    // The error dialog is rendered only while open (`@if (errorDialog.activeError())`),
    // so a push-on-construct / dispose-on-destroy keeps the stack honest.
    // The previous implementation had no Escape handling at all - now Escape
    // routes through the central stack like every other modal.
    inject(ModalStackService).pushUntilDestroyed(
      'error-dialog',
      () => this.closeRequest.emit(),
      inject(DestroyRef),
    );
  }
}
