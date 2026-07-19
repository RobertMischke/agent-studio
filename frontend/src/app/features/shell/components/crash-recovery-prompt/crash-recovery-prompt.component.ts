import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, effect, inject, signal } from '@angular/core';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import type { CrashRecoveryPending } from '../../../../models/task.model';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { TaskService } from '../../../../services/task.service';

@Component({
  selector: 'app-crash-recovery-prompt',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, DialogComponent, StudioIconComponent],
  templateUrl: './crash-recovery-prompt.component.html',
  styleUrl: './crash-recovery-prompt.component.scss',
})
export class CrashRecoveryPromptComponent implements OnInit {
  private readonly tasks = inject(TaskService);
  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);

  readonly pending = signal<CrashRecoveryPending[]>([]);
  readonly loading = signal(false);
  readonly busyId = signal<string | null>(null);
  readonly busyAll = signal(false);
  readonly error = signal<string | null>(null);
  readonly open = computed(() => this.pending().length > 0);

  private stackDispose: (() => void) | null = null;

  constructor() {
    effect(() => {
      if (this.open()) {
        if (!this.stackDispose) {
          this.stackDispose = this.modalStack.push('crash-recovery-prompt', () => true);
        }
      } else if (this.stackDispose) {
        this.stackDispose();
        this.stackDispose = null;
      }
    });
    this.destroyRef.onDestroy(() => this.stackDispose?.());
  }

  ngOnInit(): void {
    this.refresh();
  }

  refresh(): void {
    this.loading.set(true);
    this.error.set(null);
    this.tasks.getPendingCrashRecoveries().subscribe({
      next: (res) => {
        this.pending.set(res.pending ?? []);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(this.errorMessage(err, 'Could not load pending crash recovery items.'));
        this.loading.set(false);
      },
    });
  }

  commit(item: CrashRecoveryPending): void {
    if (this.busyId() || this.busyAll()) return;
    this.busyId.set(item.id);
    this.error.set(null);
    this.tasks.commitCrashRecovery(item.id).subscribe({
      next: () => {
        this.busyId.set(null);
        this.remove(item.id);
      },
      error: (err) => {
        this.busyId.set(null);
        this.error.set(this.errorMessage(err, 'Could not commit crash recovery changes.'));
      },
    });
  }

  /// Dismisses every pending item in sequence. Already-dismissed items stay
  /// removed when a later one fails; the error then names the failing item
  /// and the rest remain reviewable.
  dismissAll(): void {
    if (this.busyId() || this.busyAll()) return;
    this.busyAll.set(true);
    this.error.set(null);
    const queue = [...this.pending()];
    const next = () => {
      const item = queue.shift();
      if (!item) {
        this.busyAll.set(false);
        return;
      }
      this.tasks.dismissCrashRecovery(item.id).subscribe({
        next: () => {
          this.remove(item.id);
          next();
        },
        error: (err) => {
          this.busyAll.set(false);
          this.error.set(this.errorMessage(err, `Could not dismiss '${item.projectName}'.`));
        },
      });
    };
    next();
  }

  dismiss(item: CrashRecoveryPending): void {
    if (this.busyId() || this.busyAll()) return;
    this.busyId.set(item.id);
    this.error.set(null);
    this.tasks.dismissCrashRecovery(item.id).subscribe({
      next: () => {
        this.busyId.set(null);
        this.remove(item.id);
      },
      error: (err) => {
        this.busyId.set(null);
        this.error.set(this.errorMessage(err, 'Could not dismiss crash recovery item.'));
      },
    });
  }

  fileCountLabel(item: CrashRecoveryPending): string {
    const count = item.files?.length ?? 0;
    return `${count} changed file${count === 1 ? '' : 's'}`;
  }

  private remove(id: string): void {
    this.pending.update(items => items.filter(item => item.id !== id));
  }

  private errorMessage(err: unknown, fallback: string): string {
    const anyErr = err as { error?: { error?: string }; message?: string };
    return anyErr?.error?.error || anyErr?.message || fallback;
  }
}
