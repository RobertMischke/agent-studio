import {
  ChangeDetectionStrategy,
  Component,
  inject,
} from '@angular/core';
import { NotificationService } from '../../../services/notification.service';
import { NotificationKind } from '../../../models/app-dialog.model';

/**
 * Stack of transient notification toasts (success / info / warning / error).
 * Mounted once at the app shell; visibility driven by
 * `NotificationService.notifications()`.
 *
 * Modal/blocking error feedback continues to live in `ErrorDialogService` —
 * this component is for non-blocking outcomes such as "Lane cleared",
 * "Settings saved", or "Auto-pickup resumed". The toast itself, the icon,
 * and the title share the same Catppuccin-inspired panel design as the
 * confirm dialog so every notification surface feels coherent.
 */
@Component({
  selector: 'app-notification-stack',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './notification-stack.component.html',
})
export class NotificationStackComponent {
  private readonly service = inject(NotificationService);
  readonly notifications = this.service.notifications;

  dismiss(id: number): void {
    this.service.dismiss(id);
  }

  iconFor(kind: NotificationKind): string {
    switch (kind) {
      case 'success': return '✓';
      case 'info':    return 'ℹ';
      case 'warning': return '⚠';
      case 'error':   return '⚠';
    }
  }

  ariaLiveFor(kind: NotificationKind): 'polite' | 'assertive' {
    return kind === 'error' || kind === 'warning' ? 'assertive' : 'polite';
  }
}
