import { Component, computed, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { JobColumnComponent } from './components/job-column';
import { JobDetailComponent } from './components/job-detail';
import { JobService } from './services/job.service';
import { JobDetail, JobInfo, GroupedJobs, WatchPathEntry } from './models/job.model';

@Component({
  selector: 'app-root',
  imports: [JobColumnComponent, JobDetailComponent, FormsModule],
  template: `
    <div class="app">
      <header class="header">
        <div class="header__brand">
          <span class="header__icon">🔭</span>
          <h1 class="header__title">Orchestrator</h1>
          <span class="header__subtitle">AI Work Monitor</span>
        </div>
        <div class="header__filters">
          @for (name of projectNames(); track name) {
            <button class="filter-chip"
                    [class.filter-chip--active]="isProjectActive(name)"
                    (click)="toggleProject(name)">
              @if (getRunnerIndicator(name); as indicator) {
                <span class="runner-dot" [class]="'runner-dot--' + indicator.cls">{{ indicator.icon }}</span>
              }
              {{ name }}
            </button>
          }
        </div>
        <div class="header__actions">
          <button class="btn btn--create" (click)="openCreate()">
            ＋ New Task
          </button>
          <button class="btn btn--refresh" (click)="refresh()" [disabled]="jobService.loading()">
            {{ jobService.loading() ? '⏳' : '🔄' }} Refresh
          </button>
        </div>
      </header>

      <div class="layout" [class.layout--panel-open]="selectedJob()">
        <main class="dashboard">
          <app-job-column title="In Preparation" icon="📋" state="1-preparation" [jobs]="filteredGrouped().preparation" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" />
          <app-job-column title="Ready" icon="📦" state="2-ready" [jobs]="filteredGrouped().ready" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" />
          <app-job-column title="In Progress" icon="🔵" state="3-progress" [jobs]="filteredGrouped().progress" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" />
          <app-job-column title="Review" icon="🟡" state="4-review" [jobs]="filteredGrouped().review" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" />
          <app-job-column title="Completed" icon="🟢" state="5-completed" [jobs]="filteredGrouped().completed" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" />
        </main>

        @if (selectedJob(); as detail) {
          <aside class="detail-panel" [style.width.px]="panelWidth()">
            <div class="detail-panel__resize" (mousedown)="startResize($event)"></div>
            <app-job-detail [detail]="detail" [watchPaths]="watchPaths()" (back)="closeDetail()" (fileSaved)="onFileSaved()" (projectChanged)="onProjectChanged($event)" />
          </aside>
        }
      </div>

      @if (showCreate()) {
        <div class="overlay" (click)="cancelCreate()">
          <div class="create-dialog" (click)="$event.stopPropagation()">
            <h2 class="create-dialog__title">New Task</h2>
            <label class="field">
              <span class="field__label">Title</span>
              <input class="field__input" [(ngModel)]="newTitle" placeholder="Task title" />
            </label>
            <label class="field">
              <span class="field__label">Project</span>
              <select class="field__input" [(ngModel)]="newWatchPath">
                @for (wp of watchPaths(); track wp.path) {
                  <option [value]="wp.path">{{ wp.name }}</option>
                }
              </select>
            </label>
            <label class="field">
              <span class="field__label">Agent</span>
              <input class="field__input" [(ngModel)]="newAgent" placeholder="copilot" />
            </label>
            <label class="field">
              <span class="field__label">Prompt (optional)</span>
              <textarea class="field__input field__textarea" [(ngModel)]="newPrompt" rows="5" placeholder="Task description..."></textarea>
            </label>
            <div class="create-dialog__actions">
              <button class="btn" (click)="cancelCreate()">Cancel</button>
              <button class="btn btn--primary" (click)="submitCreate()" [disabled]="!newTitle.trim()">Create</button>
            </div>
          </div>
        </div>
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
    .header__actions { display: flex; gap: 12px; }
    .header__filters { display: flex; gap: 8px; align-items: center; }
    .filter-chip {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.1);
      color: #94a3b8;
      padding: 5px 14px;
      border-radius: 20px;
      cursor: pointer;
      font-size: 12px;
      font-weight: 500;
      transition: all 0.15s;
    }
    .filter-chip:hover { background: rgba(255,255,255,0.1); color: #e2e8f0; }
    .filter-chip--active {
      background: rgba(139,92,246,0.2);
      border-color: rgba(139,92,246,0.4);
      color: #c4b5fd;
    }
    .filter-chip--active:hover { background: rgba(139,92,246,0.3); }
    .runner-dot { font-size: 10px; margin-right: 2px; }
    .runner-dot--running { animation: pulse-runner 1.5s infinite; }
    @keyframes pulse-runner {
      0%, 100% { opacity: 1; }
      50% { opacity: 0.4; }
    }
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
    .btn--create {
      background: rgba(139,92,246,0.15);
      border-color: rgba(139,92,246,0.3);
      color: #a78bfa;
    }
    .btn--create:hover { background: rgba(139,92,246,0.25); }
    .btn--primary {
      background: #6366f1;
      border-color: #6366f1;
      color: white;
    }
    .btn--primary:hover { background: #5558e6; }

    .overlay {
      position: fixed;
      inset: 0;
      background: rgba(0,0,0,0.6);
      display: grid;
      place-items: center;
      z-index: 100;
    }
    .create-dialog {
      background: #1e1e2e;
      border: 1px solid rgba(255,255,255,0.1);
      border-radius: 16px;
      padding: 24px;
      width: 480px;
      max-width: 90vw;
    }
    .create-dialog__title { margin: 0 0 20px; font-size: 18px; }
    .create-dialog__actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; }
    .field { display: flex; flex-direction: column; gap: 4px; margin-bottom: 12px; }
    .field__label { font-size: 12px; color: #94a3b8; text-transform: uppercase; letter-spacing: 0.5px; }
    .field__input {
      background: rgba(0,0,0,0.3);
      border: 1px solid rgba(255,255,255,0.1);
      color: #e2e8f0;
      padding: 8px 12px;
      border-radius: 8px;
      font-size: 13px;
    }
    .field__input:focus { outline: none; border-color: #6366f1; }
    .field__textarea { font-family: 'Consolas', monospace; resize: vertical; }

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
  readonly showCreate = signal(false);
  readonly watchPaths = signal<WatchPathEntry[]>([]);
  readonly activeProjects = signal<Set<string>>(new Set(JSON.parse(localStorage.getItem('activeProjects') ?? '[]')));

  readonly projectNames = computed(() => {
    return this.watchPaths().map(wp => wp.name);
  });

  readonly filteredGrouped = computed(() => {
    const grouped = this.jobService.grouped();
    const active = this.activeProjects();
    if (active.size === 0) return grouped;
    const filterJobs = (jobs: JobInfo[]) => jobs.filter(j => active.has(j.projectName));
    return {
      preparation: filterJobs(grouped.preparation),
      ready: filterJobs(grouped.ready),
      progress: filterJobs(grouped.progress),
      review: filterJobs(grouped.review),
      completed: filterJobs(grouped.completed),
    } as GroupedJobs;
  });

  newTitle = '';
  newWatchPath = '';
  newAgent = 'copilot';
  newPrompt = '';

  constructor(readonly jobService: JobService) {}

  ngOnInit() {
    this.refresh();
    this.jobService.getWatchPaths().subscribe({
      next: (entries) => {
        this.watchPaths.set(entries);
        if (entries.length > 0) this.newWatchPath = entries[0].path;
      }
    });
    this.jobService.refreshRunnerStatus();
  }

  refresh() {
    this.jobService.refresh();
  }

  openDetail(job: JobInfo) {
    this.jobService.getDetail(job.id, job.watchPath).subscribe({
      next: (detail) => this.selectedJob.set(detail),
    });
  }

  closeDetail() {
    this.selectedJob.set(null);
  }

  onJobDrop(event: { jobId: string; watchPath: string; targetState: string }) {
    this.jobService.moveJob(event.jobId, event.targetState, event.watchPath).subscribe({
      next: () => this.refresh(),
      error: (err) => this.jobService.error.set(err.message || 'Failed to move job'),
    });
  }

  onJobReorder(event: { state: string; jobs: { jobId: string; watchPath: string }[] }) {
    this.jobService.reorderJobs(event.jobs).subscribe({
      next: () => this.refresh(),
      error: (err) => this.jobService.error.set(err.message || 'Failed to reorder'),
    });
  }

  openCreate() {
    this.showCreate.set(true);
  }

  cancelCreate() {
    this.showCreate.set(false);
    this.newTitle = '';
    this.newPrompt = '';
    this.newAgent = 'copilot';
  }

  submitCreate() {
    this.jobService.createJob({
      title: this.newTitle.trim(),
      watchPath: this.newWatchPath,
      agent: this.newAgent || 'copilot',
      promptMarkdown: this.newPrompt.trim() || undefined
    }).subscribe({
      next: () => {
        this.cancelCreate();
        this.refresh();
      },
      error: (err) => this.jobService.error.set(err.error || 'Failed to create job'),
    });
  }

  toggleProject(name: string) {
    const current = new Set(this.activeProjects());
    if (current.has(name)) {
      current.delete(name);
    } else {
      current.add(name);
    }
    this.activeProjects.set(current);
    localStorage.setItem('activeProjects', JSON.stringify([...current]));
  }

  isProjectActive(name: string): boolean {
    return this.activeProjects().has(name);
  }

  getRunnerIndicator(name: string): { icon: string; cls: string } | null {
    const status = this.jobService.runnerStatus();
    const runner = status.projects[name];
    if (!runner) return null;
    if (runner.activeJobId) return { icon: '🔵', cls: 'running' };
    if (runner.mode === 'paused') return { icon: '⏸', cls: 'paused' };
    if (runner.mode === 'auto-continuous') return { icon: '🟢', cls: 'idle' };
    if (runner.mode === 'auto-single') return { icon: '🟢', cls: 'idle' };
    return null;
  }

  onFileSaved() {
    // Re-fetch detail to reflect changes
      const current = this.selectedJob();
      if (current) {
      this.jobService.getDetail(current.info.id, current.info.watchPath).subscribe({
        next: (detail) => this.selectedJob.set(detail),
      });
    }
  }

  onProjectChanged(targetWatchPath: string) {
    const current = this.selectedJob();
    this.closeDetail();
    this.jobService.refresh();
    if (current) {
      // Re-open detail after refresh
      setTimeout(() => {
        this.jobService.getDetail(current.info.id, targetWatchPath).subscribe({
          next: (detail) => this.selectedJob.set(detail),
          error: () => {} // job might not be found if moved across drives
        });
      }, 500);
    }
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
