import { ChangeDetectionStrategy, Component, inject, input, output, signal } from '@angular/core';
import { TaskService } from '../../services/task.service';
import { NotificationService } from '../../services/notification.service';

@Component({
  selector: 'app-release-task-button',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './release-task-button.component.html',
  styleUrl: './release-task-button.component.scss',
})
export class ReleaseTaskButtonComponent {
  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);

  readonly targetId = input.required<string>();
  readonly targetKey = input.required<string>();
  readonly watchPath = input<string | undefined>(undefined);
  readonly released = input<boolean>(false);
  readonly expanded = input<boolean>(false);
  readonly releaseChanged = output<boolean>();
  readonly busy = signal(false);

  setReleased(event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    if (this.busy()) return;
    const next = !this.released();
    this.busy.set(true);
    this.tasks.setTaskReleased(this.targetId(), next, this.watchPath()).subscribe({
      next: () => {
        this.busy.set(false);
        this.releaseChanged.emit(next);
        this.notifications.success(
          next
            ? `${this.targetKey()} released for dependents`
            : `Release withdrawn for ${this.targetKey()}`,
        );
        this.tasks.refresh(true);
      },
      error: () => {
        this.busy.set(false);
        this.notifications.error(`Could not update the release flag for ${this.targetKey()}`);
      },
    });
  }
}
