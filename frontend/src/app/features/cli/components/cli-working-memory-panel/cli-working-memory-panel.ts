import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import type { CliWorkingMemoryEntry, CliWorkingMemoryReport } from '../../../../features/cli';
import type { CliType } from '../../../../models/task.model';
import { cliTypeIcon, cliTypeLabel, formatRelativeTime } from '../../../../services/format.util';

/**
 * Per-CLI Working-Memory panel for the Admin/CLI page (ASS-1748 / T1c). For each
 * installed CLI it lists the persistent memory / session state the backend found
 * on disk (path, size, last-used, content preview) and lets the operator delete
 * a single memory / session state after an inline confirm.
 *
 * The auth-safety guarantee is enforced server-side: auth / credential and
 * base-config entries arrive with `deletable=false`, are rendered as protected
 * with a lock, and have no delete affordance. Even if a delete were somehow
 * issued for one, `CliWorkingMemoryService.Delete` refuses it - clearing a CLI's
 * working memory can never log the operator out.
 */
@Component({
  selector: 'app-cli-working-memory-panel',
  standalone: true,
  imports: [],
  templateUrl: './cli-working-memory-panel.html',
  styleUrl: './cli-working-memory-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CliWorkingMemoryPanelComponent implements OnInit {
  private readonly jobService = inject(TaskService);

  readonly cliTypes: CliType[] = ['claude', 'codex', 'gemini'];

  readonly reports = signal<Record<string, CliWorkingMemoryReport | null>>({});
  readonly loading = signal(false);
  readonly errorMsg = signal<string | null>(null);
  readonly notice = signal<string | null>(null);

  // Two-step delete: the first click arms `pendingDelete` (entry id), the
  // confirm click performs it. `deleting` holds the id of an in-flight delete.
  readonly pendingDelete = signal<string | null>(null);
  readonly deleting = signal<string | null>(null);

  ngOnInit(): void {
    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.errorMsg.set(null);
    this.notice.set(null);
    this.pendingDelete.set(null);
    let pending = this.cliTypes.length;
    const finish = () => {
      pending--;
      if (pending === 0) this.loading.set(false);
    };
    for (const cli of this.cliTypes) {
      this.jobService.getCliWorkingMemory(cli).subscribe({
        next: (report) => {
          this.reports.update((r) => ({ ...r, [cli]: report }));
          finish();
        },
        error: () => {
          this.errorMsg.set('Failed to load working memory for one or more CLIs.');
          this.reports.update((r) => ({ ...r, [cli]: null }));
          finish();
        },
      });
    }
  }

  reportFor(cli: CliType): CliWorkingMemoryReport | null {
    return this.reports()[cli] ?? null;
  }

  requestDelete(entry: CliWorkingMemoryEntry): void {
    this.notice.set(null);
    this.pendingDelete.set(entry.id);
  }

  cancelDelete(): void {
    this.pendingDelete.set(null);
  }

  confirmDelete(cli: CliType, entry: CliWorkingMemoryEntry): void {
    this.deleting.set(entry.id);
    this.errorMsg.set(null);
    this.jobService.deleteCliWorkingMemory(cli, entry.path).subscribe({
      next: (result) => {
        this.deleting.set(null);
        this.pendingDelete.set(null);
        if (result.report) {
          this.reports.update((r) => ({ ...r, [cli]: result.report }));
        }
        if (result.status === 'Deleted') {
          this.notice.set(`Deleted '${entry.label}' (${this.formatBytes(result.freedBytes)} freed).`);
        } else {
          // Protected / NotFound / Error all land here defensively even though
          // the UI never offers delete for a protected row.
          this.errorMsg.set(result.message ?? `Could not delete '${entry.label}'.`);
        }
      },
      error: () => {
        this.deleting.set(null);
        this.pendingDelete.set(null);
        this.errorMsg.set(`Delete request failed for '${entry.label}'.`);
      },
    });
  }

  isPending(entry: CliWorkingMemoryEntry): boolean {
    return this.pendingDelete() === entry.id;
  }

  isDeleting(entry: CliWorkingMemoryEntry): boolean {
    return this.deleting() === entry.id;
  }

  icon(t: CliType): string { return cliTypeIcon(t); }
  label(t: CliType): string { return cliTypeLabel(t); }

  kindLabel(kind: string): string {
    switch (kind) {
      case 'memory': return 'Memory';
      case 'session': return 'Session';
      case 'auth': return 'Auth';
      case 'config': return 'Config';
      default: return kind;
    }
  }

  formatRelative(dateStr: string | null): string {
    return dateStr ? formatRelativeTime(dateStr, Date.now()) : '';
  }

  formatBytes(n: number): string {
    if (!n || n < 0) return '0 B';
    if (n < 1024) return `${n} B`;
    const units = ['KB', 'MB', 'GB', 'TB'];
    let value = n / 1024;
    let i = 0;
    while (value >= 1024 && i < units.length - 1) {
      value /= 1024;
      i++;
    }
    return `${value.toFixed(value >= 10 ? 0 : 1)} ${units[i]}`;
  }
}
