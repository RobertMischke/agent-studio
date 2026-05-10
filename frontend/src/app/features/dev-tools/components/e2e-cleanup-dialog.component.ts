import { Component, EventEmitter, OnInit, Output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DevToolsService, E2EJob, DeleteE2EReport } from '../../../services/dev-tools.service';

type Phase = 'loading' | 'list' | 'deleting' | 'report' | 'error';

/**
 * Lists every job whose id or title matches "E2E" (case-insensitive)
 * across every watched project, with all rows selected by default so
 * the common "wipe leftover playwright fixtures" flow is one click.
 *
 * Backed by:
 *   GET  /api/devtools/e2e-jobs
 *   POST /api/devtools/e2e-jobs/delete
 */
@Component({
  selector: 'app-e2e-cleanup-dialog',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './e2e-cleanup-dialog.component.html',
  styleUrl: './e2e-cleanup-dialog.component.scss'
})
export class E2ECleanupDialogComponent implements OnInit {
  @Output() closed = new EventEmitter<void>();
  @Output() didDelete = new EventEmitter<void>();

  readonly phase = signal<Phase>('loading');
  readonly jobs = signal<E2EJob[]>([]);
  readonly selected = signal<Set<string>>(new Set());
  readonly errorText = signal<string>('');
  readonly report = signal<DeleteE2EReport | null>(null);
  readonly pendingCount = signal(0);

  constructor(private devTools: DevToolsService) {}

  ngOnInit(): void { this.load(); }

  load(): void {
    this.phase.set('loading');
    this.devTools.listE2EJobs().subscribe({
      next: (jobs) => {
        this.jobs.set(jobs);
        // Pre-select everything: the common case is "kill all of them".
        this.selected.set(new Set(jobs.map(j => j.jobKey)));
        this.phase.set('list');
      },
      error: (err) => {
        this.errorText.set(err?.error?.error || err?.message || 'Failed to load E2E jobs');
        this.phase.set('error');
      }
    });
  }

  selectedCount(): number { return this.selected().size; }
  allSelected(): boolean { return this.jobs().length > 0 && this.selected().size === this.jobs().length; }
  someSelected(): boolean { return this.selected().size > 0; }

  toggle(key: string, checked: boolean): void {
    const next = new Set(this.selected());
    if (checked) next.add(key); else next.delete(key);
    this.selected.set(next);
  }

  toggleAll(checked: boolean): void {
    if (checked) this.selected.set(new Set(this.jobs().map(j => j.jobKey)));
    else this.selected.set(new Set());
  }

  confirmDelete(): void {
    const keys = [...this.selected()];
    if (keys.length === 0) return;
    if (!window.confirm(`Delete ${keys.length} E2E job(s) across all projects?`)) return;
    this.pendingCount.set(keys.length);
    this.phase.set('deleting');
    this.devTools.deleteE2EJobs(keys).subscribe({
      next: (rep) => {
        this.report.set(rep);
        this.phase.set('report');
        if (rep.deletedCount > 0) this.didDelete.emit();
      },
      error: (err) => {
        this.errorText.set(err?.error?.error || err?.message || 'Delete failed');
        this.phase.set('error');
      }
    });
  }

  onBackdropClick(): void {
    if (this.phase() === 'deleting') return;
    this.closed.emit();
  }
}
