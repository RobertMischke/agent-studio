import { Component, input, output } from '@angular/core';
import { JobDetail } from '../models/job.model';

@Component({
  selector: 'app-job-detail',
  standalone: true,
  template: `
    <div class="detail">
      <div class="detail__header">
        <button class="detail__back" (click)="back.emit()">← Back</button>
        <h2 class="detail__title">{{ detail().info.title || detail().info.id }}</h2>
        <span class="detail__state" [class]="'state--' + detail().info.state">{{ detail().info.state }}</span>
      </div>

      <div class="detail__tabs">
        @for (tab of tabs; track tab.id) {
          <button class="tab" [class.tab--active]="activeTab === tab.id" (click)="activeTab = tab.id">
            {{ tab.icon }} {{ tab.label }}
          </button>
        }
      </div>

      <div class="detail__content">
        @switch (activeTab) {
          @case ('overview') {
            <div class="section">
              <h3>Prompt</h3>
              <pre class="md-content">{{ detail().promptMarkdown || 'No prompt.md found' }}</pre>
            </div>
            <div class="section">
              <h3>Status</h3>
              <pre class="md-content">{{ detail().statusMarkdown || 'No status.md found' }}</pre>
            </div>
          }
          @case ('files') {
            <div class="section">
              <h3>Artifacts ({{ detail().artifacts.length }})</h3>
              <ul class="file-list">
                @for (f of detail().artifacts; track f) {
                  <li>📄 {{ f }}</li>
                }
              </ul>
              @if (detail().artifacts.length === 0) {
                <p class="empty">No artifacts yet</p>
              }
            </div>
          }
          @case ('screenshots') {
            <div class="section">
              <h3>Screenshots ({{ detail().screenshots.length }})</h3>
              @for (s of detail().screenshots; track s) {
                <div class="screenshot-name">🖼️ {{ s }}</div>
              }
              @if (detail().screenshots.length === 0) {
                <p class="empty">No screenshots</p>
              }
            </div>
          }
          @case ('logs') {
            <div class="section">
              <h3>Logs ({{ detail().logs.length }})</h3>
              <ul class="file-list">
                @for (l of detail().logs; track l) {
                  <li>📋 {{ l }}</li>
                }
              </ul>
              @if (detail().logs.length === 0) {
                <p class="empty">No logs</p>
              }
            </div>
          }
          @case ('metrics') {
            @if (detail().metrics; as m) {
              <div class="metrics-grid">
                <div class="metric"><span class="metric__value">{{ m.durationMinutes }}</span><span class="metric__label">Minutes</span></div>
                <div class="metric"><span class="metric__value">{{ m.filesChanged }}</span><span class="metric__label">Files changed</span></div>
                <div class="metric"><span class="metric__value">+{{ m.linesAdded }}</span><span class="metric__label">Lines added</span></div>
                <div class="metric"><span class="metric__value">-{{ m.linesRemoved }}</span><span class="metric__label">Lines removed</span></div>
                <div class="metric"><span class="metric__value">{{ m.reworkCount }}</span><span class="metric__label">Reworks</span></div>
                <div class="metric"><span class="metric__value">{{ m.buildSuccess === null ? '–' : m.buildSuccess ? '✅' : '❌' }}</span><span class="metric__label">Build</span></div>
              </div>
            } @else {
              <p class="empty">No metrics available</p>
            }
          }
          @case ('review') {
            <div class="section">
              <pre class="md-content">{{ detail().reviewMarkdown || 'No review.md found' }}</pre>
            </div>
          }
          @case ('timeline') {
            <div class="timeline">
              @for (entry of detail().timeline; track entry.timestamp) {
                <div class="timeline__entry">
                  <span class="timeline__time">{{ formatTime(entry.timestamp) }}</span>
                  <span class="timeline__event">{{ entry.event }}</span>
                  @if (entry.detail) {
                    <span class="timeline__detail">{{ entry.detail }}</span>
                  }
                </div>
              }
              @if (detail().timeline.length === 0) {
                <p class="empty">No timeline entries</p>
              }
            </div>
          }
        }
      </div>
    </div>
  `,
  styles: [`
    .detail { padding: 0; }
    .detail__header {
      display: flex;
      align-items: center;
      gap: 16px;
      margin-bottom: 20px;
    }
    .detail__back {
      background: rgba(255,255,255,0.06);
      border: none;
      color: #94a3b8;
      padding: 8px 14px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 13px;
    }
    .detail__back:hover { background: rgba(255,255,255,0.1); }
    .detail__title { margin: 0; font-size: 20px; color: #e2e8f0; flex: 1; }
    .detail__state {
      font-size: 12px;
      text-transform: uppercase;
      padding: 4px 10px;
      border-radius: 6px;
      font-weight: 600;
    }
    .state--running { background: rgba(59,130,246,0.15); color: #3b82f6; }
    .state--review-needed { background: rgba(245,158,11,0.15); color: #f59e0b; }
    .state--accepted { background: rgba(16,185,129,0.15); color: #10b981; }
    .state--rejected { background: rgba(239,68,68,0.15); color: #ef4444; }
    .state--draft { background: rgba(107,114,128,0.15); color: #6b7280; }

    .detail__tabs {
      display: flex;
      gap: 4px;
      margin-bottom: 20px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
      padding-bottom: 8px;
      overflow-x: auto;
    }
    .tab {
      background: none;
      border: none;
      color: #64748b;
      padding: 8px 12px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 13px;
      white-space: nowrap;
    }
    .tab:hover { background: rgba(255,255,255,0.04); }
    .tab--active { background: rgba(255,255,255,0.08); color: #e2e8f0; font-weight: 600; }

    .section { margin-bottom: 20px; }
    .section h3 { font-size: 14px; color: #94a3b8; margin: 0 0 8px; }
    .md-content {
      background: rgba(0,0,0,0.2);
      padding: 16px;
      border-radius: 8px;
      white-space: pre-wrap;
      font-size: 13px;
      color: #cbd5e1;
      border: 1px solid rgba(255,255,255,0.04);
    }
    .file-list {
      list-style: none;
      padding: 0;
      margin: 0;
    }
    .file-list li {
      padding: 6px 10px;
      font-size: 13px;
      color: #94a3b8;
      border-bottom: 1px solid rgba(255,255,255,0.04);
    }
    .empty { color: #4a5568; font-size: 13px; text-align: center; padding: 20px; }

    .metrics-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
      gap: 12px;
    }
    .metric {
      background: rgba(0,0,0,0.2);
      border-radius: 10px;
      padding: 16px;
      text-align: center;
      border: 1px solid rgba(255,255,255,0.04);
    }
    .metric__value { display: block; font-size: 24px; font-weight: 700; color: #e2e8f0; }
    .metric__label { display: block; font-size: 11px; color: #64748b; margin-top: 4px; }

    .timeline { display: flex; flex-direction: column; gap: 8px; }
    .timeline__entry {
      display: flex;
      gap: 12px;
      align-items: baseline;
      padding: 8px 12px;
      background: rgba(0,0,0,0.15);
      border-radius: 8px;
    }
    .timeline__time { font-size: 11px; color: #64748b; min-width: 120px; }
    .timeline__event { font-size: 13px; color: #e2e8f0; }
    .timeline__detail { font-size: 12px; color: #94a3b8; }

    .screenshot-name { padding: 8px; font-size: 13px; color: #94a3b8; }
  `]
})
export class JobDetailComponent {
  readonly detail = input.required<JobDetail>();
  readonly back = output<void>();

  activeTab = 'overview';

  tabs = [
    { id: 'overview', label: 'Overview', icon: '📋' },
    { id: 'files', label: 'Files', icon: '📄' },
    { id: 'screenshots', label: 'Screenshots', icon: '🖼️' },
    { id: 'logs', label: 'Logs', icon: '📝' },
    { id: 'metrics', label: 'Metrics', icon: '📊' },
    { id: 'review', label: 'Review', icon: '✅' },
    { id: 'timeline', label: 'Timeline', icon: '⏱️' },
  ];

  formatTime(dateStr: string): string {
    return new Date(dateStr).toLocaleString();
  }
}
