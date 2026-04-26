import { Component, computed, effect, OnInit, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { JobColumnComponent } from './components/job-column';
import { JobDetailComponent } from './components/job-detail';
import { JobService } from './services/job.service';
import { JobDetail, JobInfo, GroupedJobs, WatchPathEntry } from './models/job.model';
import { ErrorDialogService } from './services/error-dialog.service';

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

      <div class="layout" [class.layout--focus]="selectedJob()">
        @if (selectedJob(); as detail) {
          <div class="workspace">
            <aside class="task-nav">
              <div class="task-nav__header">
                <button class="btn btn--ghost" (click)="closeDetail()">← Board</button>
                <div>
                  <div class="task-nav__eyebrow">Task list</div>
                  <h2 class="task-nav__title">Focused view</h2>
                </div>
              </div>

              <div class="task-nav__groups">
                @for (group of focusGroups(); track group.state) {
                  <section class="task-nav__group">
                    <div class="task-nav__group-header">
                      <span>{{ group.icon }} {{ group.title }}</span>
                      <span class="task-nav__count">{{ group.jobs.length }}</span>
                    </div>

                    @if (group.jobs.length > 0) {
                      <div class="task-nav__items">
                        @for (job of group.jobs; track job.jobKey) {
                          <button class="task-nav__item"
                                  [class.task-nav__item--active]="isSelectedJob(job)"
                                  (click)="openDetail(job)">
                            <span class="task-nav__item-title">{{ job.title || job.id }}</span>
                            <span class="task-nav__item-meta">
                              <span>#{{ job.order }}</span>
                              <span>{{ job.projectName }}</span>
                            </span>
                          </button>
                        }
                      </div>
                    }
                  </section>
                }
              </div>
            </aside>

            <main class="workspace__main">
              <app-job-detail [detail]="detail" [watchPaths]="watchPaths()" (back)="closeDetail()" (fileSaved)="onFileSaved()" (projectChanged)="onProjectChanged($event)" />
            </main>
          </div>
        } @else {
          <main class="dashboard">
            <app-job-column title="In Preparation" icon="📋" state="1-preparation" [jobs]="filteredGrouped().preparation" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" />
            <app-job-column title="Ready" icon="📦" state="2-ready" [jobs]="filteredGrouped().ready" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" />
            <app-job-column title="In Progress" icon="🔵" state="3-progress" [jobs]="filteredGrouped().progress" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" />
            <app-job-column title="Review" icon="🟡" state="4-review" [jobs]="filteredGrouped().review" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" />
            <app-job-column title="Completed" icon="🟢" state="5-completed" [jobs]="filteredGrouped().completed" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" />
          </main>
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

      @if (errorDialog.activeError(); as error) {
        <div class="overlay overlay--error" (click)="closeErrorDialog()">
          <div class="error-dialog" (click)="$event.stopPropagation()">
            <div class="error-dialog__header">
              <div>
                <div class="error-dialog__eyebrow">Error details</div>
                <h2 class="error-dialog__title">{{ error.title }}</h2>
              </div>
              <button class="error-dialog__close" type="button" (click)="closeErrorDialog()">✕</button>
            </div>

            @if (error.source) {
              <div class="error-dialog__source">{{ error.source }}</div>
            }

            <div class="error-dialog__message">{{ error.message }}</div>

            <div class="error-dialog__actions">
              <button class="btn" type="button" (click)="copyErrorDetails()">{{ copyErrorButtonLabel() }}</button>
              @if (error.canOpenCliConfig && selectedJob()) {
                <button class="btn btn--primary" type="button" (click)="openCliConfigFromError()">🔧 Configure CLI</button>
              }
            </div>

            <section class="error-dialog__section">
              <div class="error-dialog__section-title">Output</div>
              <pre class="error-dialog__code">{{ error.output }}</pre>
            </section>

            <section class="error-dialog__section">
              <div class="error-dialog__section-title">Stack trace</div>
              @if (error.stackTrace) {
                <pre class="error-dialog__code">{{ error.stackTrace }}</pre>
              } @else {
                <div class="error-dialog__empty">No stack trace available for this error.</div>
              }
            </section>
          </div>
        </div>
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
    .overlay--error {
      z-index: 120;
      padding: 24px;
      align-items: start;
      overflow-y: auto;
    }
    .error-dialog {
      background: #11111b;
      border: 1px solid rgba(248,113,113,0.28);
      border-radius: 18px;
      padding: 24px;
      width: min(860px, 100%);
      box-shadow: 0 24px 80px rgba(0,0,0,0.45);
      display: flex;
      flex-direction: column;
      gap: 16px;
    }
    .error-dialog__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 16px;
    }
    .error-dialog__eyebrow {
      font-size: 11px;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: #fca5a5;
      margin-bottom: 6px;
    }
    .error-dialog__title {
      margin: 0;
      font-size: 22px;
      color: #ffe4e6;
    }
    .error-dialog__close {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.08);
      color: #f8fafc;
      width: 36px;
      height: 36px;
      border-radius: 999px;
      cursor: pointer;
      font-size: 16px;
    }
    .error-dialog__close:hover { background: rgba(255,255,255,0.1); }
    .error-dialog__source {
      font-size: 12px;
      color: #fda4af;
      padding: 8px 10px;
      border-radius: 10px;
      background: rgba(244,63,94,0.08);
      border: 1px solid rgba(244,63,94,0.18);
      width: fit-content;
      max-width: 100%;
      word-break: break-word;
    }
    .error-dialog__message {
      font-size: 15px;
      line-height: 1.6;
      color: #ffe4e6;
      padding: 14px 16px;
      border-radius: 14px;
      background: rgba(244,63,94,0.1);
      border: 1px solid rgba(244,63,94,0.18);
    }
    .error-dialog__actions {
      display: flex;
      justify-content: flex-end;
      gap: 10px;
      flex-wrap: wrap;
    }
    .error-dialog__section {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .error-dialog__section-title {
      font-size: 12px;
      color: #94a3b8;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      font-weight: 700;
    }
    .error-dialog__code {
      margin: 0;
      padding: 16px;
      border-radius: 14px;
      background: rgba(0,0,0,0.32);
      border: 1px solid rgba(255,255,255,0.08);
      color: #e2e8f0;
      font-size: 12px;
      line-height: 1.55;
      font-family: 'Consolas', 'SFMono-Regular', monospace;
      overflow: auto;
      max-height: 280px;
      white-space: pre-wrap;
      word-break: break-word;
    }
    .error-dialog__empty {
      padding: 14px 16px;
      border-radius: 14px;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.06);
      color: #94a3b8;
      font-size: 13px;
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
      min-height: calc(100vh - 70px);
      transition: all 0.3s ease;
    }
    .layout--focus {
      padding: 24px;
    }
    .dashboard {
      display: flex;
      gap: 16px;
      padding: 24px;
      overflow-x: auto;
      flex: 1;
      min-width: 0;
    }
    .workspace {
      display: grid;
      grid-template-columns: minmax(240px, 300px) minmax(0, 1fr);
      gap: 24px;
      width: 100%;
      min-height: calc(100vh - 118px);
      animation: slideIn 0.25s ease;
    }
    .workspace__main {
      min-width: 0;
    }
    .task-nav {
      background: #181825;
      border: 1px solid rgba(255,255,255,0.06);
      border-radius: 20px;
      padding: 20px;
      display: flex;
      flex-direction: column;
      gap: 18px;
      max-height: calc(100vh - 118px);
      position: sticky;
      top: 24px;
      overflow: hidden;
    }
    .task-nav__header {
      display: flex;
      flex-direction: column;
      gap: 14px;
      padding-bottom: 16px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
    }
    .task-nav__eyebrow {
      font-size: 11px;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: #64748b;
      margin-bottom: 4px;
    }
    .task-nav__title {
      margin: 0;
      font-size: 20px;
      color: #e2e8f0;
    }
    .task-nav__groups {
      display: flex;
      flex-direction: column;
      gap: 16px;
      overflow-y: auto;
      padding-right: 4px;
    }
    .task-nav__group {
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .task-nav__group-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 8px;
      font-size: 12px;
      color: #94a3b8;
      font-weight: 600;
    }
    .task-nav__count {
      background: rgba(255,255,255,0.08);
      border-radius: 999px;
      padding: 2px 8px;
      font-size: 11px;
      color: #cbd5e1;
    }
    .task-nav__items {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .task-nav__item {
      width: 100%;
      text-align: left;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.06);
      color: #cbd5e1;
      border-radius: 14px;
      padding: 12px 14px;
      display: flex;
      flex-direction: column;
      gap: 8px;
      cursor: pointer;
      transition: border-color 0.15s ease, background 0.15s ease, transform 0.15s ease;
    }
    .task-nav__item:hover {
      background: rgba(255,255,255,0.06);
      border-color: rgba(255,255,255,0.12);
      transform: translateY(-1px);
    }
    .task-nav__item--active {
      background: rgba(99,102,241,0.16);
      border-color: rgba(99,102,241,0.45);
      box-shadow: 0 0 0 1px rgba(99,102,241,0.15);
    }
    .task-nav__item-title {
      font-size: 14px;
      font-weight: 600;
      color: #f8fafc;
      line-height: 1.4;
    }
    .task-nav__item-meta {
      display: flex;
      justify-content: space-between;
      gap: 8px;
      font-size: 11px;
      color: #94a3b8;
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .btn--ghost {
      justify-self: flex-start;
      width: fit-content;
      color: #cbd5e1;
    }
    @keyframes slideIn {
      from { transform: translateX(20px); opacity: 0; }
      to { transform: translateX(0); opacity: 1; }
    }
    @media (max-width: 1200px) {
      .header {
        align-items: flex-start;
        flex-wrap: wrap;
        gap: 12px;
      }
      .header__filters {
        flex-wrap: wrap;
      }
      .workspace {
        grid-template-columns: 1fr;
      }
      .task-nav {
        position: static;
        max-height: none;
      }
    }
  `]
})
export class App implements OnInit {
  readonly selectedJob = signal<JobDetail | null>(null);
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

  readonly focusGroups = computed(() => {
    const grouped = this.filteredGrouped();
    return [
      { state: '1-preparation', title: 'In Preparation', icon: '📋', jobs: grouped.preparation },
      { state: '2-ready', title: 'Ready', icon: '📦', jobs: grouped.ready },
      { state: '3-progress', title: 'In Progress', icon: '🔵', jobs: grouped.progress },
      { state: '4-review', title: 'Review', icon: '🟡', jobs: grouped.review },
      { state: '5-completed', title: 'Completed', icon: '🟢', jobs: grouped.completed }
    ];
  });

  newTitle = '';
  newWatchPath = '';
  newAgent = 'copilot';
  newPrompt = '';

  constructor(readonly jobService: JobService, readonly errorDialog: ErrorDialogService) {
    effect(() => {
      const selected = this.selectedJob();
      const jobs = this.jobService.jobs();

      if (!selected) {
        return;
      }

      const latest = jobs.find(job => job.jobKey === selected.info.jobKey);
      if (!latest) {
        return;
      }

      const currentExecution = selected.info.execution;
      const latestExecution = latest.execution;
      const executionChanged =
        (currentExecution?.status ?? null) !== (latestExecution?.status ?? null) ||
        (currentExecution?.processId ?? null) !== (latestExecution?.processId ?? null) ||
        (currentExecution?.exitCode ?? null) !== (latestExecution?.exitCode ?? null) ||
        (currentExecution?.durationSeconds ?? null) !== (latestExecution?.durationSeconds ?? null);

      if (selected.info.state === latest.state && !executionChanged) {
        return;
      }

      untracked(() => {
        this.jobService.getDetail(latest.id, latest.watchPath).subscribe({
          next: (detail) => this.selectedJob.set(detail),
        });
      });
    });
  }

  ngOnInit() {
    this.refresh();
    this.jobService.startLiveUpdates();
    this.jobService.getWatchPaths().subscribe({
      next: (entries) => {
        this.watchPaths.set(entries);
        if (entries.length > 0) this.newWatchPath = entries[0].path;
      },
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Failed to load projects',
          fallbackMessage: 'Failed to load projects',
          source: 'Project list'
        });
      },
    });
    this.jobService.refreshRunnerStatus();
  }

  refresh() {
    this.jobService.refresh();
  }

  openDetail(job: JobInfo) {
    this.jobService.getDetail(job.id, job.watchPath).subscribe({
      next: (detail) => this.selectedJob.set(detail),
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Failed to load task details',
          fallbackMessage: 'Failed to load task details',
          source: `Task ${job.id}`
        });
      }
    });
  }

  isSelectedJob(job: JobInfo): boolean {
    return this.selectedJob()?.info.jobKey === job.jobKey;
  }

  closeDetail() {
    this.selectedJob.set(null);
  }

  onJobDrop(event: { jobId: string; watchPath: string; targetState: string }) {
    this.jobService.moveJob(event.jobId, event.targetState, event.watchPath).subscribe({
      next: () => this.refresh(),
      error: (err) => {
        this.jobService.error.set(err.message || 'Failed to move job');
        this.errorDialog.show(err, {
          title: 'Failed to move task',
          fallbackMessage: 'Failed to move task',
          source: `Task ${event.jobId}`
        });
      },
    });
  }

  onJobReorder(event: { state: string; jobs: { jobId: string; watchPath: string }[] }) {
    this.jobService.reorderJobs(event.jobs).subscribe({
      next: () => this.refresh(),
      error: (err) => {
        this.jobService.error.set(err.message || 'Failed to reorder');
        this.errorDialog.show(err, {
          title: 'Failed to reorder tasks',
          fallbackMessage: 'Failed to reorder tasks',
          source: `Column ${event.state}`
        });
      },
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
      error: (err) => {
        this.jobService.error.set(err.error || 'Failed to create job');
        this.errorDialog.show(err, {
          title: 'Failed to create task',
          fallbackMessage: 'Failed to create task',
          source: 'Task creation'
        });
      },
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
          error: (err) => {
            this.errorDialog.show(err, {
              title: 'Task moved, but detail view could not be reopened',
              fallbackMessage: 'Task moved, but detail view could not be reopened automatically.',
              source: `Task ${current.info.id}`
            });
          }
        });
      }, 500);
    }
  }

  closeErrorDialog() {
    this.errorDialog.close();
  }

  copyErrorDetails() {
    this.errorDialog.copyActiveError();
  }

  copyErrorButtonLabel(): string {
    switch (this.errorDialog.copyState()) {
      case 'copied':
        return 'Copied';
      case 'failed':
        return 'Copy failed';
      default:
        return 'Copy output';
    }
  }

  openCliConfigFromError() {
    this.errorDialog.requestCliConfig();
  }
}
