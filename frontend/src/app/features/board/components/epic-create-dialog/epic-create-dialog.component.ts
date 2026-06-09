import { AfterViewInit, ChangeDetectionStrategy, Component, ElementRef, ViewChild, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import type { CliType } from '../../../../models/task.model';
import { TaskState } from '../../../../models/task.model';

/**
 * Focused "create an epic" modal opened from the epic overview screen
 * (empty-state invitation or the header "+ New epic" button). Unlike the
 * full create-task dialog, an epic only needs a title and an optional
 * description, so this surface stays intentionally small.
 *
 * The new epic is a {@link CreateJobRequest} with `kind=epic`, landed in
 * `0-backlog` so its creation never trips the pickup gate into an
 * immediate decomposition run (an epic in `2-ready` is auto-picked as a
 * planning run; see {@link EpicRunPolicy}). The CLI/model carried over are
 * the user's stored defaults so a later move to Ready decomposes with the
 * agent they expect. On success it emits {@link created} so the host can
 * reload the rollup; the host owns the open/close signal.
 */
@Component({
  selector: 'app-epic-create-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DialogComponent],
  templateUrl: './epic-create-dialog.component.html',
  styleUrl: './epic-create-dialog.component.scss',
})
export class EpicCreateDialogComponent implements AfterViewInit {
  /** Project the epic lands in (display name, shown in the header). */
  readonly projectName = input.required<string>();
  /** Resolved watch path the create call targets. */
  readonly watchPath = input.required<string>();

  readonly created = output<{ id: string }>();
  readonly cancelled = output<void>();

  private readonly jobService = inject(TaskService);
  private readonly notifications = inject(NotificationService);

  readonly draftTitle = signal('');
  readonly draftDescription = signal('');
  readonly submitting = signal(false);
  readonly errorMsg = signal<string | null>(null);

  @ViewChild('titleInput') private titleInputRef?: ElementRef<HTMLInputElement>;

  readonly canSubmit = computed(() =>
    this.draftTitle().trim().length > 0 && !this.submitting(),
  );

  ngAfterViewInit(): void {
    queueMicrotask(() => this.titleInputRef?.nativeElement.focus());
  }

  onCancel(): void {
    if (this.submitting()) return;
    this.cancelled.emit();
  }

  onSubmit(): void {
    if (!this.canSubmit()) return;
    const title = this.draftTitle().trim();
    const description = this.draftDescription().trim();
    const cli = readDefaultCliPref();
    const model = readDefaultModelPref(cli);

    this.submitting.set(true);
    this.errorMsg.set(null);
    this.jobService.createJob({
      title,
      watchPath: this.watchPath(),
      agent: cli,
      cliType: cli,
      model: model || undefined,
      promptMarkdown: description || undefined,
      kind: 'epic',
      targetState: TaskState.Backlog,
    }).subscribe({
      next: (res) => {
        this.submitting.set(false);
        this.notifications.success(`Epic "${title}" created in ${this.projectName()}.`);
        this.jobService.refresh(true);
        this.created.emit({ id: res.id });
      },
      error: (err: unknown) => {
        this.submitting.set(false);
        const msg = formatError(err);
        this.errorMsg.set(msg);
        this.notifications.error(`Could not create epic "${title}": ${msg}`);
      },
    });
  }

  onTitleKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.onSubmit();
    }
  }
}

function readDefaultCliPref(): CliType {
  const stored = localStorage.getItem('defaultCliType') as CliType | null;
  return stored ?? 'claude';
}

function readDefaultModelPref(cli: CliType): string {
  return localStorage.getItem('defaultModel:' + cli) ?? '';
}

function formatError(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    const body = err.error as { error?: string } | null;
    if (body?.error) return body.error;
    if (err.status === 0) return 'Backend unreachable. Try again in a moment.';
    return `Create failed (HTTP ${err.status}).`;
  }
  return 'Create failed.';
}
