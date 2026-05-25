import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
} from '@angular/core';
import { NotificationService } from '../../../services/notification.service';
import { NotificationComponent } from '../../notification/notification.component';
import { NotificationAction } from '../../../models/app-dialog.model';

/**
 * Stack of persistent notification toasts (success / info / warning / error).
 * Mounted once at the app shell; visibility driven by
 * `NotificationService.notifications()`.
 *
 * F56: all notifications render as toasts in a top-right stack. Click-to-dismiss
 * is the default; auto-dismiss only for short success/info without actions.
 * Escape dismisses the topmost toast (when no modal has consumed the event).
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

  constructor() {
    if (typeof document !== 'undefined') {
      const destroyRef = inject(DestroyRef);
      const ac = new AbortController();
      destroyRef.onDestroy(() => ac.abort());
      // Bubble-phase listener. If a modal-stack entry consumed the Escape
      // in capture phase (via stopImmediatePropagation + preventDefault),
      // this handler never fires. Otherwise it dismisses the topmost toast.
      document.addEventListener('keydown', (e) => {
        if (e.key !== 'Escape') return;
        if (e.defaultPrevented) return;
        if (this.notifications().length === 0) return;
        e.preventDefault();
        this.service.dismissTopmost();
      }, { signal: ac.signal });
    }
  }

  dismiss(id: number): void {
    this.service.dismiss(id);
  }

  onAction(action: NotificationAction, toastId: number): void {
    action.callback();
    this.service.dismiss(toastId);
  }
}
