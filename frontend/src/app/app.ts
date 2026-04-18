import { Component, OnInit, signal } from '@angular/core';
import { JobColumnComponent } from './components/job-column';
import { JobDetailComponent } from './components/job-detail';
import { JobService } from './services/job.service';
import { JobDetail, JobInfo } from './models/job.model';

@Component({
  selector: 'app-root',
  imports: [JobColumnComponent, JobDetailComponent],
  template: `
    <div class="app">
      <header class="header">
        <div class="header__brand">
          <span class="header__icon">🔭</span>
          <h1 class="header__title">Orchestrator</h1>
          <span class="header__subtitle">AI Work Monitor</span>
        </div>
        <div class="header__actions">
          <button class="btn btn--refresh" (click)="refresh()" [disabled]="jobService.loading()">
            {{ jobService.loading() ? '⏳' : '🔄' }} Refresh
          </button>
        </div>
      </header>

      <div class="layout" [class.layout--panel-open]="selectedJob()">
        <main class="dashboard">
          <app-job-column title="In Preparation" icon="📋" state="1-preparation" [jobs]="jobService.grouped().preparation" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" />
          <app-job-column title="Ready" icon="📦" state="2-ready" [jobs]="jobService.grouped().ready" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" />
          <app-job-column title="In Progress" icon="🔵" state="3-progress" [jobs]="jobService.grouped().progress" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" />
          <app-job-column title="Review" icon="🟡" state="4-review" [jobs]="jobService.grouped().review" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" />
          <app-job-column title="Completed" icon="🟢" state="5-completed" [jobs]="jobService.grouped().completed" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" />
        </main>

        @if (selectedJob(); as detail) {
          <aside class="detail-panel" [style.width.px]="panelWidth()">
            <div class="detail-panel__resize" (mousedown)="startResize($event)"></div>
            <app-job-detail [detail]="detail" (back)="closeDetail()" />
          </aside>
        }
      </div>

      @if (jobService.error(); as err) {
        <div class="error-bar">⚠️ {{ err }}</div>
      }
    </div>
  `,
  styles: [`
    .app {
      min-height: 100vh;
      background: #0f0f1a;
      color: #e2e8f0;
      font-family: 'Segoe UI', system-ui, sans-serif;
    }
    .header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      padding: 16px 24px;
      background: #181825;
      border-bottom: 1px solid rgba(255,255,255,0.06);
    }
    .header__brand {
      display: flex;
      align-items: center;
      gap: 10px;
    }
    .header__icon { font-size: 24px; }
    .header__title { margin: 0; font-size: 20px; font-weight: 700; }
    .header__subtitle { font-size: 13px; color: #64748b; }
    .btn {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.08);
      color: #e2e8f0;
      padding: 8px 16px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 13px;
    }
    .btn:hover { background: rgba(255,255,255,0.1); }
    .btn:disabled { opacity: 0.5; cursor: not-allowed; }

    .layout {
      display: flex;
      min-height: calc(100vh - 70px);
      transition: all 0.3s ease;
    }
    .dashboard {
      display: flex;
      gap: 16px;
      padding: 24px;
      overflow-x: auto;
      flex: 1;
      min-width: 0;
    }
    .detail-panel {
      width: 520px;
      min-width: 320px;
      max-width: 60vw;
      background: #181825;
      border-left: 1px solid rgba(255,255,255,0.06);
      padding: 24px;
      overflow-y: auto;
      max-height: calc(100vh - 70px);
      animation: slideIn 0.25s ease;
      position: relative;
      flex-shrink: 0;
    }
    .detail-panel__resize {
      position: absolute;
      left: 0; top: 0; bottom: 0;
      width: 5px;
      cursor: col-resize;
      background: transparent;
      z-index: 10;
      transition: background 0.15s;
    }
    .detail-panel__resize:hover,
    .detail-panel__resize:active {
      background: rgba(99,102,241,0.5);
    }
    @keyframes slideIn {
      from { transform: translateX(20px); opacity: 0; }
      to { transform: translateX(0); opacity: 1; }
    }
    .error-bar {
      position: fixed;
      bottom: 16px;
      left: 50%;
      transform: translateX(-50%);
      background: rgba(239,68,68,0.9);
      color: white;
      padding: 10px 20px;
      border-radius: 8px;
      font-size: 13px;
    }
  `]
})
export class App implements OnInit {
  readonly selectedJob = signal<JobDetail | null>(null);
  readonly panelWidth = signal(+(localStorage.getItem('panelWidth') ?? 520));

  constructor(readonly jobService: JobService) {}

  ngOnInit() {
    this.refresh();
  }

  refresh() {
    this.jobService.refresh();
  }

  openDetail(job: JobInfo) {
    this.jobService.getDetail(job.id).subscribe({
      next: (detail) => this.selectedJob.set(detail),
    });
  }

  closeDetail() {
    this.selectedJob.set(null);
  }

  onJobDrop(event: { jobId: string; targetState: string }) {
    this.jobService.moveJob(event.jobId, event.targetState).subscribe({
      next: () => this.refresh(),
      error: (err) => this.jobService.error.set(err.message || 'Failed to move job'),
    });
  }

  startResize(event: MouseEvent) {
    event.preventDefault();
    const startX = event.clientX;
    const startWidth = this.panelWidth();

    const onMove = (e: MouseEvent) => {
      const delta = startX - e.clientX;
      const newWidth = Math.min(Math.max(startWidth + delta, 320), window.innerWidth * 0.6);
      this.panelWidth.set(newWidth);
    };

    const onUp = () => {
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
      localStorage.setItem('panelWidth', String(this.panelWidth()));
    };

    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
  }
}
