import { Component, input, output } from '@angular/core';
import { JobDetail } from '../models/job.model';

@Component({
  selector: 'app-job-detail',
  standalone: true,
  template: `
    <div class="detail">
      <div class="detail__header">
        <button class="detail__back" (click)="back.emit()">←</button>
        <h2 class="detail__title">{{ detail().info.title || detail().info.id }}</h2>
        <span class="detail__state" [class]="'state--' + detail().info.state">
          {{ stateLabel(detail().info.state) }}
        </span>
      </div>

      <div class="detail__meta">
        <span>{{ detail().info.agent }}</span>
        <span>{{ detail().info.priority }}</span>
        <span>{{ formatDate(detail().info.createdAt) }}</span>
      </div>

      @if (detail().promptMarkdown) {
        <section class="section">
          <h3 class="section__title">Prompt</h3>
          <pre class="section__body">{{ detail().promptMarkdown }}</pre>
        </section>
      }

      @if (detail().statusMarkdown) {
        <section class="section">
          <h3 class="section__title">Status</h3>
          <pre class="section__body">{{ detail().statusMarkdown }}</pre>
        </section>
      }

      @if (detail().log.length > 0) {
        <section class="section">
          <h3 class="section__title">Protocol</h3>
          <div class="log">
            @for (entry of detail().log; track entry.timestamp) {
              <div class="log__row">
                <span class="log__time">{{ formatTime(entry.timestamp) }}</span>
                <span class="log__event">{{ entry.event }}</span>
                @if (entry.detail) {
                  <span class="log__detail">{{ entry.detail }}</span>
                }
              </div>
            }
          </div>
        </section>
      }
    </div>
  `,
  styles: [`
    .detail { padding: 0; }

    .detail__header {
      display: flex;
      align-items: center;
      gap: 12px;
      margin-bottom: 8px;
    }
    .detail__back {
      background: rgba(255,255,255,0.06);
      border: none;
      color: #94a3b8;
      width: 32px; height: 32px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 16px;
      display: grid; place-items: center;
    }
    .detail__back:hover { background: rgba(255,255,255,0.1); }
    .detail__title { margin: 0; font-size: 18px; color: #e2e8f0; flex: 1; }
    .detail__state {
      font-size: 11px;
      text-transform: uppercase;
      padding: 4px 10px;
      border-radius: 6px;
      font-weight: 600;
      letter-spacing: 0.4px;
    }
    .state--1-preparation { background: rgba(139,92,246,0.15); color: #8b5cf6; }
    .state--2-ready { background: rgba(6,182,212,0.15); color: #06b6d4; }
    .state--3-progress { background: rgba(59,130,246,0.15); color: #3b82f6; }
    .state--4-review { background: rgba(245,158,11,0.15); color: #f59e0b; }
    .state--5-completed { background: rgba(16,185,129,0.15); color: #10b981; }

    .detail__meta {
      display: flex;
      gap: 16px;
      font-size: 12px;
      color: #64748b;
      margin-bottom: 24px;
      padding-bottom: 16px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
    }

    .section { margin-bottom: 24px; }
    .section__title {
      font-size: 12px;
      color: #64748b;
      text-transform: uppercase;
      letter-spacing: 0.5px;
      margin: 0 0 8px;
    }
    .section__body {
      background: rgba(0,0,0,0.2);
      padding: 16px;
      border-radius: 8px;
      white-space: pre-wrap;
      word-break: break-word;
      font-size: 13px;
      line-height: 1.6;
      color: #cbd5e1;
      border: 1px solid rgba(255,255,255,0.04);
      margin: 0;
    }

    .log { display: flex; flex-direction: column; gap: 2px; }
    .log__row {
      display: flex;
      gap: 12px;
      align-items: baseline;
      padding: 8px 12px;
      background: rgba(0,0,0,0.15);
      border-radius: 6px;
      font-size: 13px;
    }
    .log__time { font-size: 11px; color: #64748b; min-width: 70px; font-variant-numeric: tabular-nums; }
    .log__event { color: #e2e8f0; }
    .log__detail { color: #94a3b8; font-size: 12px; }
  `]
})
export class JobDetailComponent {
  readonly detail = input.required<JobDetail>();
  readonly back = output<void>();

  stateLabel(state: string): string {
    return state.replace(/^\d+-/, '');
  }

  formatTime(dateStr: string): string {
    return new Date(dateStr).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  formatDate(dateStr: string): string {
    return new Date(dateStr).toLocaleDateString();
  }
}
