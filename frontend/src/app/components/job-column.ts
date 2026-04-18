import { Component, input, output } from '@angular/core';
import { JobInfo } from '../models/job.model';
import { JobCardComponent } from './job-card';

@Component({
  selector: 'app-job-column',
  standalone: true,
  imports: [JobCardComponent],
  template: `
    <div class="column">
      <div class="column__header">
        <span class="column__icon">{{ icon() }}</span>
        <h2 class="column__title">{{ title() }}</h2>
        <span class="column__count">{{ jobs().length }}</span>
      </div>
      <div class="column__body">
        @for (job of jobs(); track job.id) {
          <app-job-card [job]="job" (click)="jobClick.emit(job)" />
        }
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
  `]
})
export class JobColumnComponent {
  readonly title = input.required<string>();
  readonly icon = input<string>('');
  readonly jobs = input.required<JobInfo[]>();
  readonly jobClick = output<JobInfo>();
}
