import { ChangeDetectionStrategy, Component, computed, effect, inject, input, model, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TaskService } from '../../../../services/task.service';
import type { EpicRollup } from '../../../../models/task.model';

/**
 * Assignment way 1 in the Create dialog: when creating a `kind=task`, an
 * optional "Parent epic" dropdown lets the user attach the new card to an
 * existing epic of the same project. The chosen id flows up via
 * `parentEpicId` and the dialog sends it as `CreateJobRequest.epicId`.
 *
 * Extracted into its own component so the create-task-dialog stays inside
 * its size budget. Epics are fetched once (GET /api/epics returns every
 * project's epics) and filtered client-side by the selected project's
 * `watchPath`, so switching the project in the dialog re-scopes the list
 * without another round-trip. The picker self-hides when `show` is false
 * (kind=epic) or the project has no epics.
 */
@Component({
  selector: 'app-create-epic-picker',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  templateUrl: './create-epic-picker.component.html',
  styleUrl: './create-epic-picker.component.scss',
})
export class CreateEpicPickerComponent {
  /** Selected project's watch path; scopes the epic list. */
  readonly watchPath = input<string>('');
  /** False when the dialog's kind is `epic` (an epic has no parent epic). */
  readonly show = input<boolean>(true);
  /** Two-way: the chosen parent epic id, or '' for none. */
  readonly parentEpicId = model<string>('');

  private readonly jobs = inject(TaskService);
  private readonly allEpics = signal<EpicRollup[]>([]);

  /** Epics that belong to the currently selected project. */
  readonly epics = computed(() =>
    this.allEpics().filter((e) => e.watchPath === this.watchPath()),
  );

  readonly visible = computed(() => this.show() && this.epics().length > 0);

  constructor() {
    this.jobs.getEpics().subscribe({
      next: (list) => this.allEpics.set(list ?? []),
      error: () => this.allEpics.set([]),
    });

    // Drop a stale selection when the project changes so a cross-project
    // epicId never reaches the create call. Guarded by the early return
    // so resetting to '' does not re-trigger the effect.
    effect(() => {
      const selected = this.parentEpicId();
      if (!selected) return;
      if (!this.epics().some((e) => e.id === selected)) this.parentEpicId.set('');
    });
  }
}
