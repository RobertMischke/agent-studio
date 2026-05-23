import {
  ChangeDetectionStrategy,
  Component,
  inject,
} from '@angular/core';
import { NotificationService } from '../../../services/notification.service';
import { NotificationComponent } from '../../notification/notification.component';

/**
 * Stack of transient notification toasts (success / info / warning / error).
 * Mounted once at the app shell; visibility driven by
 * `NotificationService.notifications()`.
 *
 * The visual primitive (icon + body + close + per-severity tinting + light
 * & dark theming) is owned by `<app-notification>` (F37). This component
 * is now just the positioning host (top-right pile, gap, max width) and
 * iterates over the service signal. Modal/blocking error feedback
 * continues to live in `ErrorDialogService`.
 */
@Component({
  selector: 'app-notification-stack',
  standalone: true,
  imports: [NotificationComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './notification-stack.component.html',
})
export class NotificationStackComponent {
  private readonly service = inject(NotificationService);
  readonly notifications = this.service.notifications;

  dismiss(id: number): void {
    this.service.dismiss(id);
  }
}
