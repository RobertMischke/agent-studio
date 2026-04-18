import { Component, input } from '@angular/core';
import { JobInfo } from '../models/job.model';

@Component({
  selector: 'app-job-card',
  standalone: true,
  template: `
    <div class="job-card" [class]="'job-card--' + job().state">
      <div class="job-card__header">
        <span class="job-card__state">{{ stateLabel() }}</span>
        <span class="job-card__priority" [class]="'priority--' + job().priority">{{ job().priority }}</span>
      </div>
      <h3 class="job-card__title">{{ job().title || job().id }}</h3>
      <div class="job-card__meta">
        <span class="job-card__agent">🤖 {{ job().agent || 'unknown' }}</span>
        <span class="job-card__size">{{ formatSize(job().totalSizeBytes) }}</span>
      </div>
      <div class="job-card__activity">
        Last activity: {{ timeAgo(job().lastActivity) }}
      </div>
    </div>
  `,
  styles: [`
    .job-card {
      background: var(--card-bg, #1e1e2e);
      border: 1px solid var(--border, #333);
      border-radius: 12px;
      padding: 16px;
      cursor: pointer;
      transition: transform 0.15s, box-shadow 0.15s;
      border-left: 4px solid var(--state-color, #555);
    }
    .job-card:hover {
      transform: translateY(-2px);
      box-shadow: 0 4px 12px rgba(0,0,0,0.3);
    }
    .job-card--preparation { --state-color: #8b5cf6; }
    .job-card--ready { --state-color: #06b6d4; }
    .job-card--progress { --state-color: #3b82f6; }
    .job-card--review { --state-color: #f59e0b; }
    .job-card--completed { --state-color: #10b981; }

    .job-card__header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 8px;
    }
    .job-card__state {
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--state-color);
      font-weight: 600;
    }
    .job-card__priority {
      font-size: 10px;
      padding: 2px 6px;
      border-radius: 4px;
      background: rgba(255,255,255,0.08);
    }
    .priority--high { color: #ef4444; }
    .priority--normal { color: #9ca3af; }
    .priority--low { color: #6b7280; }

    .job-card__title {
      margin: 0 0 8px;
      font-size: 15px;
      font-weight: 600;
      color: #e2e8f0;
    }
    .job-card__meta {
      display: flex;
      justify-content: space-between;
      font-size: 12px;
      color: #94a3b8;
      margin-bottom: 4px;
    }
    .job-card__activity {
      font-size: 11px;
      color: #64748b;
    }
  `]
})
export class JobCardComponent {
  readonly job = input.required<JobInfo>();

  stateLabel(): string {
    return this.job().state.replace('-', ' ');
  }

  formatSize(bytes: number): string {
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
  }

  timeAgo(dateStr: string): string {
    if (!dateStr) return 'never';
    const diff = Date.now() - new Date(dateStr).getTime();
    const mins = Math.floor(diff / 60000);
    if (mins < 1) return 'just now';
    if (mins < 60) return mins + 'm ago';
    const hrs = Math.floor(mins / 60);
    if (hrs < 24) return hrs + 'h ago';
    return Math.floor(hrs / 24) + 'd ago';
  }
}
