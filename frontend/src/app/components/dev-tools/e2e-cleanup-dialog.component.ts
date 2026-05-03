import { Component, EventEmitter, OnInit, Output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DevToolsService, E2EJob, DeleteE2EReport } from '../../services/dev-tools.service';

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
  template: `
    <div class="overlay" (click)="onBackdropClick()">
      <div class="panel" (click)="$event.stopPropagation()">
        <header class="panel__head">
          <div>
            <div class="panel__eyebrow">Dev tool</div>
            <h2 class="panel__title">Delete E2E Jobs</h2>
            <p class="panel__sub">Across every watched project. Matches "E2E" anywhere in id or title.</p>
          </div>
          <button class="panel__close" (click)="closed.emit()" title="Close">×</button>
        </header>

        <div class="panel__body">
          @switch (phase()) {
            @case ('loading') {
              <div class="empty">Loading…</div>
            }
            @case ('error') {
              <div class="empty empty--error">{{ errorText() }}</div>
              <div class="actions">
                <button class="btn" (click)="load()">Retry</button>
              </div>
            }
            @case ('list') {
              @if (jobs().length === 0) {
                <div class="empty">No E2E-named jobs found.</div>
                <div class="actions">
                  <button class="btn" (click)="closed.emit()">Close</button>
                </div>
              } @else {
                <div class="toolbar">
                  <label class="check">
                    <input type="checkbox"
                           [checked]="allSelected()"
                           [indeterminate]="someSelected() && !allSelected()"
                           (change)="toggleAll($any($event.target).checked)" />
                    Select all ({{ jobs().length }})
                  </label>
                  <span class="toolbar__sub">{{ selectedCount() }} selected</span>
                </div>
                <ul class="list" data-testid="e2e-job-list">
                  @for (job of jobs(); track job.jobKey) {
                    <li class="row" [class.row--selected]="selected().has(job.jobKey)">
                      <label class="row__check">
                        <input type="checkbox"
                               [checked]="selected().has(job.jobKey)"
                               (change)="toggle(job.jobKey, $any($event.target).checked)" />
                      </label>
                      <div class="row__main">
                        <div class="row__title">{{ job.title || job.id }}</div>
                        <div class="row__meta">
                          <span class="row__chip">{{ job.projectName }}</span>
                          <span class="row__chip row__chip--state">{{ job.state }}</span>
                          <code class="row__id">{{ job.id }}</code>
                        </div>
                      </div>
                    </li>
                  }
                </ul>
                <div class="actions">
                  <button class="btn btn--ghost" (click)="closed.emit()">Cancel</button>
                  <button class="btn btn--danger"
                          data-testid="e2e-delete-confirm"
                          [disabled]="selectedCount() === 0"
                          (click)="confirmDelete()">
                    Delete {{ selectedCount() }} job{{ selectedCount() === 1 ? '' : 's' }}
                  </button>
                </div>
              }
            }
            @case ('deleting') {
              <div class="empty">Deleting {{ pendingCount() }} job(s)…</div>
            }
            @case ('report') {
              <div class="report">
                <div class="report__line"><strong>{{ report()?.deletedCount ?? 0 }}</strong> deleted</div>
                @if ((report()?.failedCount ?? 0) > 0) {
                  <div class="report__line report__line--err">
                    <strong>{{ report()?.failedCount }}</strong> failed:
                    <ul>
                      @for (k of report()?.failed ?? []; track k) { <li><code>{{ k }}</code></li> }
                    </ul>
                  </div>
                }
              </div>
              <div class="actions">
                <button class="btn" (click)="closed.emit()">Close</button>
              </div>
            }
          }
        </div>
      </div>
    </div>
  `,
  styles: [`
    .overlay { position: fixed; inset: 0; background: rgba(0,0,0,0.7); display: grid; place-items: center; z-index: 200; }
    .panel { background: #11111b; border: 1px solid rgba(244,63,94,0.35); border-radius: 14px; width: min(720px, 94vw); max-height: 90vh; display: flex; flex-direction: column; }
    .panel__head { display: flex; justify-content: space-between; align-items: flex-start; padding: 18px 22px; border-bottom: 1px solid rgba(255,255,255,0.06); gap: 16px; }
    .panel__eyebrow { font-size: 11px; letter-spacing: 0.1em; text-transform: uppercase; color: #fca5a5; margin-bottom: 4px; }
    .panel__title { margin: 0; font-size: 20px; color: #f8fafc; }
    .panel__sub { margin: 4px 0 0; font-size: 12px; color: #94a3b8; }
    .panel__close { background: rgba(255,255,255,0.06); border: 1px solid rgba(255,255,255,0.1); color: #f8fafc; width: 32px; height: 32px; border-radius: 999px; cursor: pointer; font-size: 18px; }
    .panel__body { display: flex; flex-direction: column; gap: 14px; padding: 16px 22px 22px; min-height: 0; flex: 1; overflow-y: auto; }
    .empty { padding: 18px; text-align: center; color: #94a3b8; background: rgba(255,255,255,0.03); border: 1px solid rgba(255,255,255,0.06); border-radius: 8px; }
    .empty--error { color: #fca5a5; border-color: rgba(244,63,94,0.35); background: rgba(244,63,94,0.08); }
    .toolbar { display: flex; justify-content: space-between; align-items: center; gap: 12px; }
    .toolbar__sub { font-size: 12px; color: #94a3b8; }
    .check { display: inline-flex; align-items: center; gap: 8px; font-size: 13px; color: #cbd5e1; cursor: pointer; }
    .list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; max-height: 50vh; overflow-y: auto; }
    .row { display: flex; gap: 10px; align-items: flex-start; padding: 8px 12px; border: 1px solid rgba(255,255,255,0.06); border-radius: 8px; background: rgba(255,255,255,0.02); }
    .row--selected { background: rgba(244,63,94,0.08); border-color: rgba(244,63,94,0.30); }
    .row__check { padding-top: 2px; }
    .row__main { flex: 1; min-width: 0; }
    .row__title { font-size: 13px; font-weight: 600; color: #f8fafc; word-break: break-word; }
    .row__meta { display: flex; gap: 6px; align-items: center; flex-wrap: wrap; margin-top: 4px; font-size: 11px; color: #94a3b8; }
    .row__chip { background: rgba(255,255,255,0.06); padding: 1px 8px; border-radius: 999px; }
    .row__chip--state { background: rgba(99,102,241,0.18); color: #c7d2fe; }
    .row__id { font-family: 'Consolas', monospace; color: #94a3b8; }
    .actions { display: flex; justify-content: flex-end; gap: 8px; padding-top: 6px; }
    .btn { background: rgba(255,255,255,0.10); border: 1px solid rgba(255,255,255,0.18); color: #f8fafc; padding: 6px 14px; border-radius: 6px; cursor: pointer; font-size: 12px; font-weight: 600; }
    .btn:hover:not(:disabled) { background: rgba(255,255,255,0.18); }
    .btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .btn--ghost { background: transparent; }
    .btn--danger { background: rgba(239,68,68,0.75); border-color: rgba(248,113,113,0.85); }
    .btn--danger:hover:not(:disabled) { background: rgba(239,68,68,0.9); }
    .report { padding: 12px 14px; border: 1px solid rgba(255,255,255,0.08); border-radius: 8px; background: rgba(255,255,255,0.02); }
    .report__line { font-size: 13px; color: #cbd5e1; }
    .report__line--err { margin-top: 8px; color: #fca5a5; }
  `]
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
