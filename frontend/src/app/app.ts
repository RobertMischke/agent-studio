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

      @if (selectedJob(); as detail) {
        <main class="main">
          <app-job-detail [detail]="detail" (back)="closeDetail()" />
        </main>
      } @else {
        <main class="dashboard">
          <app-job-column title="In Preparation" icon="📋" state="preparation" [jobs]="jobService.grouped().preparation" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" />
          <app-job-column title="Ready" icon="📦" state="ready" [jobs]="jobService.grouped().ready" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" />
          <app-job-column title="In Progress" icon="🔵" state="progress" [jobs]="jobService.grouped().progress" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" />
          <app-job-column title="Review" icon="🟡" state="review" [jobs]="jobService.grouped().review" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" />
          <app-job-column title="Completed" icon="🟢" state="completed" [jobs]="jobService.grouped().completed" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" />
        </main>
      }

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

    .dashboard {
      display: flex;
      gap: 16px;
      padding: 24px;
      overflow-x: auto;
      min-height: calc(100vh - 70px);
    }
    .main {
      padding: 24px;
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
}
