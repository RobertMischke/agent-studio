import { Component, computed, input, output } from '@angular/core';
import { JobInfo, JobOrderItem } from '../models/job.model';
import { JobCardComponent } from './job-card';

const ARCHIVE_VISIBLE_LIMIT = 20;

@Component({
  selector: 'app-job-column',
  standalone: true,
  imports: [JobCardComponent],
  template: `
    <div class="column"
         [class.column--dragover]="isDragOver"
         [class.column--archive]="isArchive()"
         (dragover)="onDragOver($event)"
         (dragleave)="onDragLeave($event)"
         (drop)="onDrop($event)">
      <div class="column__header">
        <span class="column__icon">{{ icon() }}</span>
        <h2 class="column__title">{{ title() }}</h2>
        <span class="column__count">{{ jobs().length }}</span>
        @if (canArchiveAll()) {
          <button type="button"
                  class="column__archive-all"
                  data-testid="archive-all-btn"
                  title="Move all completed tasks to Archive"
                  (click)="archiveAll.emit()">
            ⬇ Archive all
          </button>
        }
      </div>
      <div class="column__body">
        @if (isArchive()) {
          @for (job of archiveVisible(); track job.jobKey) {
            <button type="button"
                    class="archive-row"
                    [attr.data-testid]="'archive-row'"
                    (click)="jobClick.emit(job)">
              <span class="archive-row__date">{{ formatShortDate(job.lastActivity) }}</span>
              <span class="archive-row__project">{{ job.projectName }}</span>
              <span class="archive-row__title">{{ job.title || job.id }}</span>
            </button>
          }
          @if (jobs().length === 0) {
            <div class="column__empty">No archived jobs</div>
          } @else if (archiveOverflow() > 0) {
            <div class="archive-overflow">
              + {{ archiveOverflow() }} more in <code>6-archive/</code> folder
            </div>
          }
        } @else {
          @for (job of jobs(); track job.jobKey; let i = $index) {
            @if (!reorderDisabled()) {
              <div class="column__drop-zone"
                   [class.column__drop-zone--active]="dropIndex === i"
                   (dragover)="onCardDragOver($event, i)"
                   (dragleave)="onCardDragLeave()"
                   (drop)="onCardDrop($event, i)">
              </div>
            }
            <app-job-card
              [job]="job"
              (click)="jobClick.emit(job)"
              draggable="true"
              (dragstart)="onDragStart($event, job)" />
          }
          @if (!reorderDisabled()) {
            <div class="column__drop-zone column__drop-zone--last"
                 [class.column__drop-zone--active]="dropIndex === jobs().length"
                 (dragover)="onCardDragOver($event, jobs().length)"
                 (dragleave)="onCardDragLeave()"
                 (drop)="onCardDrop($event, jobs().length)">
            </div>
          }
          @if (jobs().length === 0) {
            <div class="column__empty">No jobs</div>
          }
          @if (canAddTask()) {
            <button type="button" class="column__add" (click)="addTask.emit(state())">
              <span class="column__add-icon">＋</span>
              <span>Add task</span>
            </button>
          }
        }
      </div>
    </div>
  `,
  styles: [`
    .column {
      background: var(--column-bg, #181825);
      border-radius: 16px;
      padding: 16px;
      min-width: 280px;
      flex: 1;
      display: flex;
      flex-direction: column;
      gap: 12px;
      transition: outline 0.15s;
    }
    .column--dragover {
      outline: 2px solid rgba(99, 102, 241, 0.6);
      outline-offset: -2px;
      background: rgba(99, 102, 241, 0.05);
    }
    .column__header {
      display: flex;
      align-items: center;
      gap: 8px;
      padding-bottom: 8px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
    }
    .column__icon { font-size: 18px; }
    .column__title {
      margin: 0;
      font-size: 14px;
      font-weight: 600;
      color: #e2e8f0;
      flex: 1;
    }
    .column__count {
      background: rgba(255,255,255,0.08);
      border-radius: 10px;
      padding: 2px 8px;
      font-size: 12px;
      color: #94a3b8;
    }
    .column__body {
      display: flex;
      flex-direction: column;
      gap: 8px;
      flex: 1;
    }
    .column__empty {
      text-align: center;
      color: #4a5568;
      font-size: 13px;
      padding: 24px 0;
    }
    .column__drop-zone {
      height: 4px;
      border-radius: 2px;
      transition: height 0.15s, background 0.15s;
    }
    .column__drop-zone--active {
      height: 8px;
      background: rgba(99, 102, 241, 0.5);
    }
    .column__add {
      margin-top: 4px;
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 6px;
      width: 100%;
      background: rgba(139, 92, 246, 0.08);
      border: 1px dashed rgba(139, 92, 246, 0.35);
      color: #a78bfa;
      padding: 10px 12px;
      border-radius: 12px;
      cursor: pointer;
      font-size: 13px;
      font-weight: 500;
      transition: background 0.15s, border-color 0.15s, color 0.15s;
    }
    .column__add:hover {
      background: rgba(139, 92, 246, 0.18);
      border-color: rgba(139, 92, 246, 0.6);
      color: #c4b5fd;
    }
    .column__add-icon {
      font-size: 16px;
      line-height: 1;
    }
    .column__archive-all {
      background: rgba(100, 116, 139, 0.12);
      border: 1px solid rgba(100, 116, 139, 0.3);
      color: #94a3b8;
      padding: 3px 8px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 11px;
      font-weight: 500;
      white-space: nowrap;
      transition: background 0.15s, border-color 0.15s, color 0.15s;
    }
    .column__archive-all:hover {
      background: rgba(100, 116, 139, 0.25);
      border-color: rgba(100, 116, 139, 0.55);
      color: #cbd5e1;
    }
    .column--archive .column__body { gap: 2px; }
    .archive-row {
      display: grid;
      grid-template-columns: auto auto 1fr;
      gap: 8px;
      align-items: baseline;
      width: 100%;
      text-align: left;
      background: transparent;
      border: 0;
      border-bottom: 1px solid rgba(255,255,255,0.04);
      color: #cbd5e1;
      padding: 4px 6px;
      font-size: 12px;
      line-height: 1.3;
      cursor: pointer;
      transition: background 0.12s, color 0.12s;
    }
    .archive-row:hover { background: rgba(255,255,255,0.05); color: #f1f5f9; }
    .archive-row__date {
      font-family: 'Consolas', monospace;
      color: #64748b;
      font-size: 11px;
      font-variant-numeric: tabular-nums;
    }
    .archive-row__project {
      font-size: 10px;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: #8b5cf6;
    }
    .archive-row__title {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .archive-overflow {
      margin-top: 8px;
      padding: 6px 8px;
      font-size: 11px;
      color: #64748b;
      border-top: 1px dashed rgba(255,255,255,0.08);
    }
    .archive-overflow code {
      background: rgba(255,255,255,0.06);
      padding: 1px 5px;
      border-radius: 4px;
      font-size: 10px;
    }
  `]
})
export class JobColumnComponent {
  readonly title = input.required<string>();
  readonly icon = input<string>('');
  readonly state = input.required<string>();
  readonly jobs = input.required<JobInfo[]>();
  readonly reorderDisabled = input<boolean>(false);
  readonly jobClick = output<JobInfo>();
  readonly jobDrop = output<{ jobId: string; watchPath: string; targetState: string }>();
  readonly jobReorder = output<{ state: string; jobs: JobOrderItem[] }>();
  readonly addTask = output<string>();
  readonly archiveAll = output<void>();

  isDragOver = false;
  dropIndex = -1;

  canAddTask(): boolean {
    const s = this.state();
    return s === '1-preparation' || s === '2-ready';
  }

  isArchive(): boolean {
    return this.state() === '6-archive';
  }

  canArchiveAll(): boolean {
    return this.state() === '5-completed';
  }

  readonly archiveVisible = computed(() => {
    if (!this.isArchive()) return [] as JobInfo[];
    return [...this.jobs()]
      .sort((a, b) => (b.lastActivity ?? '').localeCompare(a.lastActivity ?? ''))
      .slice(0, ARCHIVE_VISIBLE_LIMIT);
  });

  readonly archiveOverflow = computed(() => {
    if (!this.isArchive()) return 0;
    return Math.max(0, this.jobs().length - ARCHIVE_VISIBLE_LIMIT);
  });

  formatShortDate(iso: string | null | undefined): string {
    if (!iso) return '—';
    const d = new Date(iso);
    if (isNaN(d.getTime())) return '—';
    const yyyy = d.getFullYear();
    const mm = String(d.getMonth() + 1).padStart(2, '0');
    const dd = String(d.getDate()).padStart(2, '0');
    return `${yyyy}-${mm}-${dd}`;
  }

  onDragStart(event: DragEvent, job: JobInfo) {
    event.dataTransfer?.setData('text/plain', JSON.stringify({ jobId: job.id, watchPath: job.watchPath, jobKey: job.jobKey }));
    event.dataTransfer?.setData('application/x-source-state', job.state);
  }

  onDragOver(event: DragEvent) {
    event.preventDefault();
    this.isDragOver = true;
  }

  onDragLeave(event: DragEvent) {
    this.isDragOver = false;
  }

  onCardDragOver(event: DragEvent, index: number) {
    event.preventDefault();
    event.stopPropagation();
    this.dropIndex = index;
  }

  onCardDragLeave() {
    this.dropIndex = -1;
  }

  onCardDrop(event: DragEvent, index: number) {
    event.preventDefault();
    event.stopPropagation();
    this.isDragOver = false;
    this.dropIndex = -1;
    const payload = this.parsePayload(event.dataTransfer?.getData('text/plain'));
    const sourceState = event.dataTransfer?.getData('application/x-source-state');
    if (!payload) return;

    if (sourceState === this.state()) {
      // Reorder within same column
      const currentJobs = this.jobs().map(j => ({ jobId: j.id, watchPath: j.watchPath, jobKey: j.jobKey }));
      const fromIndex = currentJobs.findIndex(job => job.jobKey === payload.jobKey);
      if (fromIndex >= 0) {
        const [movedJob] = currentJobs.splice(fromIndex, 1);
        const insertAt = index > fromIndex ? index - 1 : index;
        currentJobs.splice(insertAt, 0, movedJob);
      }
      this.jobReorder.emit({
        state: this.state(),
        jobs: currentJobs.map(job => ({ jobId: job.jobId, watchPath: job.watchPath }))
      });
    } else {
      // Cross-column move
      this.jobDrop.emit({ jobId: payload.jobId, watchPath: payload.watchPath, targetState: this.state() });
    }
  }

  onDrop(event: DragEvent) {
    event.preventDefault();
    this.isDragOver = false;
    this.dropIndex = -1;
    const payload = this.parsePayload(event.dataTransfer?.getData('text/plain'));
    if (payload) {
      this.jobDrop.emit({ jobId: payload.jobId, watchPath: payload.watchPath, targetState: this.state() });
    }
  }

  private parsePayload(rawPayload?: string): { jobId: string; watchPath: string; jobKey: string } | null {
    if (!rawPayload) return null;
    try {
      const payload = JSON.parse(rawPayload) as { jobId?: string; watchPath?: string; jobKey?: string };
      if (!payload.jobId || !payload.watchPath || !payload.jobKey) return null;
      return { jobId: payload.jobId, watchPath: payload.watchPath, jobKey: payload.jobKey };
    } catch {
      return null;
    }
  }
}
