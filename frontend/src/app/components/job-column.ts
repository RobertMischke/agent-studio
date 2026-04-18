import { Component, input, output } from '@angular/core';
import { JobInfo } from '../models/job.model';
import { JobCardComponent } from './job-card';

@Component({
  selector: 'app-job-column',
  standalone: true,
  imports: [JobCardComponent],
  template: `
    <div class="column"
         [class.column--dragover]="isDragOver"
         (dragover)="onDragOver($event)"
         (dragleave)="onDragLeave($event)"
         (drop)="onDrop($event)">
      <div class="column__header">
        <span class="column__icon">{{ icon() }}</span>
        <h2 class="column__title">{{ title() }}</h2>
        <span class="column__count">{{ jobs().length }}</span>
      </div>
      <div class="column__body">
        @for (job of jobs(); track job.id; let i = $index) {
          <div class="column__drop-zone"
               [class.column__drop-zone--active]="dropIndex === i"
               (dragover)="onCardDragOver($event, i)"
               (dragleave)="onCardDragLeave()"
               (drop)="onCardDrop($event, i)">
          </div>
          <app-job-card
            [job]="job"
            (click)="jobClick.emit(job)"
            draggable="true"
            (dragstart)="onDragStart($event, job)" />
        }
        <div class="column__drop-zone column__drop-zone--last"
             [class.column__drop-zone--active]="dropIndex === jobs().length"
             (dragover)="onCardDragOver($event, jobs().length)"
             (dragleave)="onCardDragLeave()"
             (drop)="onCardDrop($event, jobs().length)">
        </div>
        @if (jobs().length === 0) {
          <div class="column__empty">No jobs</div>
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
  `]
})
export class JobColumnComponent {
  readonly title = input.required<string>();
  readonly icon = input<string>('');
  readonly state = input.required<string>();
  readonly jobs = input.required<JobInfo[]>();
  readonly jobClick = output<JobInfo>();
  readonly jobDrop = output<{ jobId: string; targetState: string }>();
  readonly jobReorder = output<{ state: string; jobIds: string[] }>();

  isDragOver = false;
  dropIndex = -1;

  onDragStart(event: DragEvent, job: JobInfo) {
    event.dataTransfer?.setData('text/plain', job.id);
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
    const jobId = event.dataTransfer?.getData('text/plain');
    const sourceState = event.dataTransfer?.getData('application/x-source-state');
    if (!jobId) return;

    if (sourceState === this.state()) {
      // Reorder within same column
      const currentIds = this.jobs().map(j => j.id);
      const fromIndex = currentIds.indexOf(jobId);
      if (fromIndex >= 0) {
        currentIds.splice(fromIndex, 1);
        const insertAt = index > fromIndex ? index - 1 : index;
        currentIds.splice(insertAt, 0, jobId);
      }
      this.jobReorder.emit({ state: this.state(), jobIds: currentIds });
    } else {
      // Cross-column move
      this.jobDrop.emit({ jobId, targetState: this.state() });
    }
  }

  onDrop(event: DragEvent) {
    event.preventDefault();
    this.isDragOver = false;
    this.dropIndex = -1;
    const jobId = event.dataTransfer?.getData('text/plain');
    if (jobId) {
      this.jobDrop.emit({ jobId, targetState: this.state() });
    }
  }
}
