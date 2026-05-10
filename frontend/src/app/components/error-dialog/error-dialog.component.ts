import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { ErrorDialogState } from '../../models/error-dialog.model';

/**
 * Error overlay used by the global ErrorDialogService. The parent
 * watches `errorDialog.activeError()` and renders this component when
 * an error is present.
 */
@Component({
  selector: 'app-error-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './error-dialog.component.html'
})
export class ErrorDialogComponent {
  readonly error = input.required<ErrorDialogState>();
  readonly canOpenCliConfig = input(false);
  readonly copyButtonLabel = input<string>('Copy details');

  readonly close = output<void>();
  readonly copy = output<void>();
  readonly openCliConfig = output<void>();
}
