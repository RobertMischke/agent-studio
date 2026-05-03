import { Component, computed, effect, OnInit, signal, untracked, ViewEncapsulation } from '@angular/core';
import { forkJoin } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { JobColumnComponent } from './components/job-column';
import { JobDetailComponent } from './components/job-detail';
import { CliUsageSheetComponent } from './components/cli-usage-sheet';
import { OrchestratorFeedComponent } from './components/orchestrator-feed';
import { OrchestratorSideSheetComponent } from './components/orchestrator-side-sheet/orchestrator-side-sheet.component';
import { ProjectDetailComponent } from './components/project-detail';
import { StatusBarComponent } from './components/status-bar';
import { JobService } from './services/job.service';
import { JobDetail, JobInfo, GroupedJobs, WatchPathEntry, CliType, CLI_TYPES, CliModelInfo } from './models/job.model';
import { ErrorDialogService } from './services/error-dialog.service';
import { cliTypeLabel as fmtCliTypeLabel, formatMultiplier as fmtMultiplier } from './services/format.util';
import { CreateJobDialogComponent, PendingAttachment } from './components/board/create-job-dialog/create-job-dialog.component';
import { ErrorDialogComponent } from './components/board/error-dialog/error-dialog.component';
import { ProjectAutoInfo, ProjectTabsComponent } from './components/board/project-tabs/project-tabs.component';
import { projectIdentity } from './services/project-identity.util';

@Component({
  selector: 'app-root',
  imports: [JobColumnComponent, JobDetailComponent, CliUsageSheetComponent, OrchestratorFeedComponent, OrchestratorSideSheetComponent, ProjectDetailComponent, StatusBarComponent, FormsModule, CreateJobDialogComponent, ErrorDialogComponent, ProjectTabsComponent],
  // Keep styles global to this subtree — the App shell still owns the
  // .header*, .filter-chip*, .overlay*, .create-dialog*, .error-dialog*
  // class rules used by the extracted dialogs and project-tabs.
  encapsulation: ViewEncapsulation.None,
  template: `
    <div class="app">
      <header class="header">
        <div class="header__brand">
          <img class="header__icon" src="icons/icon.svg" alt="Agent Task Processor" width="20" height="20" />
          <h1 class="header__title">
            <span class="header__title-ai">Agent</span><span class="header__title-sep"></span><span class="header__title-name">Task Processor</span>
          </h1>
        </div>
        <app-project-tabs
          [names]="projectNames()"
          [isActive]="isProjectActiveFn"
          [runnerIndicator]="getRunnerIndicatorFn"
          [autoInfo]="getAutoInfoFn"
          (toggle)="toggleProject($event)"
          (toggleAuto)="onToggleAuto($event)"
          (openDetail)="openProjectDetail($event)" />
        <div class="header__actions">
          <button class="btn btn--create" (click)="openCreate()">
            ＋ Add Task
          </button>
        </div>
      </header>

      <div class="app__body">
        <div class="layout" [class.layout--focus]="selectedJob()">
        @if (selectedJob(); as detail) {
          <div class="workspace"
               [class.workspace--nav-collapsed]="taskNavCollapsed()"
               [style.--side-sheet-width]="sideSheetWidth() + 'px'">
            @if (taskNavCollapsed()) {
              <aside class="task-nav task-nav--collapsed" data-testid="task-nav-collapsed">
                <button class="task-nav__expand"
                        data-testid="task-nav-expand"
                        title="Expand task list"
                        (click)="setTaskNavCollapsed(false)">›</button>
                <button class="task-nav__expand task-nav__expand--board"
                        data-testid="back-to-board"
                        title="Back to board"
                        (click)="closeDetail()">←</button>
              </aside>
            } @else {
            <aside class="task-nav">
              <div class="task-nav__header">
                <div class="task-nav__header-row">
                  <button class="btn btn--ghost" data-testid="back-to-board" (click)="closeDetail()">← Board</button>
                  <button class="task-nav__collapse"
                          data-testid="task-nav-collapse"
                          title="Collapse task list"
                          (click)="setTaskNavCollapsed(true)">‹</button>
                </div>
                <div>
                  <div class="task-nav__eyebrow">Task list</div>
                  <h2 class="task-nav__title">Focused view</h2>
                </div>
              </div>

              <div class="task-nav__groups">
                @for (group of focusGroups(); track group.state) {
                  <section class="task-nav__group" [class.task-nav__group--collapsed]="isGroupCollapsed(group.state)">
                    <div class="task-nav__group-header" (click)="toggleGroupCollapse(group.state)">
                      <span>
                        <span class="task-nav__group-toggle">{{ isGroupCollapsed(group.state) ? '▶' : '▼' }}</span>
                        {{ group.icon }} {{ group.title }}
                      </span>
                      <span class="task-nav__count">{{ group.jobs.length }}</span>
                    </div>

                    @if (group.jobs.length > 0 && !isGroupCollapsed(group.state)) {
                      <div class="task-nav__items">
                        @for (job of group.jobs; track job.jobKey) {
                          <button class="task-nav__item"
                                  [class.task-nav__item--active]="isSelectedJob(job)"
                                  [style.--project-color]="identityFor(job.projectName).color"
                                  [style.--project-on]="identityFor(job.projectName).onColor"
                                  (click)="openDetail(job)">
                            <span class="task-nav__item-title">{{ job.title || job.id }}</span>
                            <span class="task-nav__item-meta">
                              <span>#{{ job.order }}</span>
                              <span class="task-nav__item-project">
                                <span class="task-nav__item-disk" aria-hidden="true">{{ identityFor(job.projectName).initial }}</span>
                                {{ job.projectName }}
                              </span>
                            </span>
                          </button>
                        }
                      </div>
                    }
                    @if (canAddTaskToGroup(group.state) && !isGroupCollapsed(group.state)) {
                      <button class="task-nav__add" (click)="openCreate(group.state)">
                        <span>＋</span>
                        <span>Add task</span>
                      </button>
                    }
                  </section>
                }
              </div>
              
              <div class="task-nav__resize-handle"
                   (mousedown)="startResize($event)"></div>
            </aside>
            }

            <main class="workspace__main">
              <app-job-detail [detail]="detail" [watchPaths]="watchPaths()" (back)="closeDetail()" (fileSaved)="onFileSaved()" (projectChanged)="onProjectChanged($event)" (completeAndNextReview)="onCompleteAndNextReview()" />
            </main>
          </div>
        } @else {
          <main class="dashboard">
            <app-job-column title="In Preparation" icon="📋" state="1-preparation" [jobs]="displayGrouped().preparation" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" (addTask)="openCreate($event)" />
            <app-job-column title="Ready" icon="📦" state="2-ready" [jobs]="displayGrouped().ready" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" (addTask)="openCreate($event)" />
            <app-job-column title="In Progress" icon="🔵" state="3-progress" [jobs]="displayGrouped().progress" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" />
            <app-job-column title="Review" icon="🟡" state="4-review" [jobs]="displayGrouped().review" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" />
            <app-job-column title="Completed" icon="🟢" state="5-completed" [jobs]="displayGrouped().completed" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" (archiveAll)="onArchiveAll()" />
            <app-job-column title="Archive" icon="🗄️" state="6-archive" [jobs]="displayGrouped().archive" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" />
          </main>
        }
      </div>

        <app-cli-usage-sheet #usageSheet class="app__sidesheet" />
        <app-orchestrator-side-sheet
          #orchSideSheet
          class="app__sidesheet"
          [projects]="projectNames()"
          [preferredProject]="orchSideSheetPreferredProject()"
          [activeJobId]="selectedJob()?.info?.id ?? null"
          [activeJobTitle]="selectedJob()?.info?.title ?? null"
          [activeWatchPath]="selectedJob()?.info?.watchPath ?? null"
          (createTaskFromDraft)="onCreateTaskFromOrchestratorDraft($event)" />
      </div>

      <app-status-bar
        [projectNames]="projectNames()"
        (toggleUsage)="usageSheet.toggle()"
        (toggleOrchestrator)="orchSideSheet.toggle()"
        (toggleFeed)="toggleOrchFeed()"
        (defaultCliChange)="onDefaultCliChange($event)"
        (defaultModelChange)="onDefaultModelChange($event)" />

      @if (orchFeedProject(); as proj) {
        <div class="overlay" (click)="closeOrchFeed()">
          <div class="overlay__panel" (click)="$event.stopPropagation()">
            <button class="overlay__close" (click)="closeOrchFeed()" title="Close">×</button>
            <app-orchestrator-feed [projectName]="proj" />
          </div>
        </div>
      }

      @if (projectDetailName(); as proj) {
        <div class="overlay" (click)="closeProjectDetail()">
          <div class="overlay__panel" (click)="$event.stopPropagation()">
            <button class="overlay__close" (click)="closeProjectDetail()" title="Close">×</button>
            <app-project-detail
              [projectName]="proj"
              (openFeed)="onOpenFeedFromDetail($event)" />
          </div>
        </div>
      }

      @if (showCreate()) {
        <app-create-job-dialog
          [title]="createDialogTitle()"
          [watchPaths]="watchPaths()"
          [availableModels]="availableModels()"
          [cliTypeDraft]="newCliType"
          [(newTitle)]="newTitle"
          [(newWatchPath)]="newWatchPath"
          [(newModel)]="newModel"
          [(newPrompt)]="newPrompt"
          [(attachments)]="newAttachments"
          (cliTypeChange)="onCreateCliTypeChange($event)"
          (cancel)="cancelCreate()"
          (submit)="submitCreate()" />
      }

      @if (errorDialog.activeError(); as error) {
        <app-error-dialog
          [error]="error"
          [canOpenCliConfig]="error.canOpenCliConfig && selectedJobUsesCopilot()"
          [copyButtonLabel]="copyErrorButtonLabel()"
          (close)="closeErrorDialog()"
          (copy)="copyErrorDetails()"
          (openCliConfig)="openCliConfigFromError()" />
      }
    </div>
  `,
  styles: [`
    .app {
      /* Use 100% of body's content box rather than 100vh so the dev-mode
         banner (22px padding-top on body) doesn't push the status bar
         below the viewport. styles.scss ensures html/body fill 100% and
         box-sizing makes padding subtract from this. */
      height: 100%;
      background: #0f0f1a;
      color: #e2e8f0;
      font-family: 'Segoe UI', system-ui, sans-serif;
      display: flex;
      flex-direction: column;
      overflow: hidden;
    }
    /* Body row holds the main layout and the CLI Usage sidesheet side-by-side.
       The sheet's :host width animates from 0 to its open width, so the layout
       reflows around it instead of being covered by an overlay.
       Body scrolls within the fixed header + status bar shell. */
    .app__body {
      flex: 1 1 auto;
      display: flex;
      flex-direction: row;
      align-items: stretch;
      min-height: 0;
      overflow: auto;
    }
    .app__body > .layout { flex: 1 1 auto; min-width: 0; }
    .app__sidesheet { align-self: stretch; }
    .header {
      flex: 0 0 auto;
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 12px;
      padding: 4px 12px;
      background: #181825;
      border-bottom: 1px solid rgba(255,255,255,0.06);
      min-height: 36px;
    }
    .header__brand {
      display: flex;
      align-items: center;
      gap: 8px;
      flex: 0 0 auto;
    }
    .header__icon {
      width: 20px;
      height: 20px;
      display: block;
      border-radius: 5px;
      box-shadow: 0 1px 4px rgba(99,102,241,0.30);
    }
    .header__title {
      margin: 0;
      font-size: 13px;
      font-weight: 800;
      letter-spacing: -0.01em;
      display: inline-flex;
      align-items: baseline;
      gap: 0;
      line-height: 1;
    }
    .header__title-ai {
      font-family: 'Segoe UI', system-ui, sans-serif;
      font-weight: 900;
      font-style: italic;
      letter-spacing: 0.02em;
      background: linear-gradient(135deg, #a5b4fc 0%, #818cf8 40%, #c4b5fd 100%);
      -webkit-background-clip: text;
      background-clip: text;
      color: transparent;
      text-shadow: 0 0 16px rgba(167,139,250,0.30);
      padding-right: 3px;
    }
    .header__title-sep {
      width: 2px;
      height: 12px;
      align-self: center;
      margin: 0 6px;
      border-radius: 2px;
      background: linear-gradient(180deg, rgba(129,140,248,0.0), rgba(129,140,248,0.85), rgba(129,140,248,0.0));
    }
    .header__title-name {
      font-weight: 600;
      letter-spacing: 0.02em;
      color: #e2e8f0;
      text-transform: uppercase;
      font-size: 11px;
    }
    .header__subtitle { font-size: 11px; color: #64748b; }
    .header__actions { display: flex; gap: 6px; flex: 0 0 auto; }
    .header__filters { display: flex; gap: 6px; align-items: center; }
    /* Filter chip carries each project's identity colour as a CSS variable
       supplied per chip; the active state pulls the chip into the project's
       hue so a five-to-ten-project header is scannable at a glance. */
    .filter-chip {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      background: rgba(255,255,255,0.06);
      border: 1px solid var(--project-border, rgba(255,255,255,0.20));
      color: #e2e8f0;
      padding: 4px 12px 4px 4px;
      border-radius: 20px;
      cursor: pointer;
      font-size: 12px;
      font-weight: 600;
      transition: all 0.15s;
    }
    .filter-chip__disk {
      display: inline-grid;
      place-items: center;
      width: 18px;
      height: 18px;
      border-radius: 999px;
      background: var(--project-color, #8b5cf6);
      color: var(--project-on, #0b1020);
      font-size: 11px;
      font-weight: 800;
      flex: 0 0 auto;
    }
    .filter-chip:hover {
      background: var(--project-soft, rgba(255,255,255,0.18));
      border-color: var(--project-border, rgba(255,255,255,0.30));
      color: #ffffff;
    }
    .filter-chip--active {
      background: var(--project-soft, rgba(139,92,246,0.45));
      border-color: var(--project-border, rgba(167,139,250,0.85));
      color: #ffffff;
      box-shadow: 0 0 0 1px var(--project-border, rgba(167,139,250,0.25)), 0 2px 6px rgba(0,0,0,0.30);
    }
    .filter-chip--active:hover {
      background: var(--project-soft, rgba(139,92,246,0.6));
      filter: brightness(1.15);
    }
    .runner-dot { font-size: 10px; margin-right: 2px; }
    .runner-dot--running { animation: pulse-runner 1.5s infinite; }
    @keyframes pulse-runner {
      0%, 100% { opacity: 1; }
      50% { opacity: 0.4; }
    }
    .project-tab {
      display: inline-flex;
      align-items: center;
      gap: 4px;
    }
    .project-tab__detail {
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.10);
      color: rgba(255,255,255,0.55);
      border-radius: 6px;
      width: 26px;
      height: 26px;
      cursor: pointer;
      font-size: 14px;
      line-height: 1;
      padding: 0;
    }
    .project-tab__detail:hover {
      background: rgba(255,255,255,0.10);
      color: #cdd6f4;
    }
    .auto-toggle {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.12);
      color: #94a3b8;
      padding: 4px 10px;
      border-radius: 16px;
      cursor: pointer;
      font-size: 11px;
      font-weight: 600;
      letter-spacing: 0.02em;
      transition: background 0.15s, border-color 0.15s, color 0.15s;
    }
    .auto-toggle:hover {
      background: rgba(255,255,255,0.10);
      border-color: rgba(255,255,255,0.22);
      color: #e2e8f0;
    }
    .auto-toggle__icon {
      font-size: 11px;
      line-height: 1;
    }
    .auto-toggle__count {
      background: rgba(255,255,255,0.12);
      border-radius: 999px;
      padding: 1px 6px;
      font-size: 10px;
      font-weight: 700;
      color: #f8fafc;
      min-width: 16px;
      text-align: center;
    }
    .auto-toggle--on {
      background: rgba(34,197,94,0.18);
      border-color: rgba(74,222,128,0.55);
      color: #bbf7d0;
      box-shadow: 0 0 0 1px rgba(74,222,128,0.18);
    }
    .auto-toggle--on:hover {
      background: rgba(34,197,94,0.28);
      border-color: rgba(74,222,128,0.75);
      color: #f0fdf4;
    }
    .auto-toggle--on .auto-toggle__count {
      background: rgba(74,222,128,0.30);
      color: #f0fdf4;
    }
    .auto-toggle--stopping {
      background: rgba(234,179,8,0.18);
      border-color: rgba(250,204,21,0.55);
      color: #fde68a;
      box-shadow: 0 0 0 1px rgba(250,204,21,0.18);
      animation: auto-stopping-pulse 2s ease-in-out infinite;
    }
    .auto-toggle--stopping:hover {
      background: rgba(234,179,8,0.28);
      border-color: rgba(250,204,21,0.75);
      color: #fef9c3;
    }
    @keyframes auto-stopping-pulse {
      0%, 100% { opacity: 1; }
      50% { opacity: 0.65; }
    }
    .btn {
      background: rgba(255,255,255,0.10);
      border: 1px solid rgba(255,255,255,0.20);
      color: #f8fafc;
      padding: 6px 14px;
      border-radius: 6px;
      cursor: pointer;
      font-size: 12px;
      font-weight: 600;
      transition: background 0.15s, border-color 0.15s;
    }
    .btn:hover { background: rgba(255,255,255,0.18); border-color: rgba(255,255,255,0.30); }
    .btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .btn--create {
      background: rgba(139,92,246,0.45);
      border-color: rgba(167,139,250,0.85);
      color: #ffffff;
      box-shadow: 0 1px 4px rgba(139,92,246,0.30);
    }
    .btn--create:hover { background: rgba(139,92,246,0.6); border-color: rgba(196,181,253,0.95); }
    .btn--primary {
      background: #6366f1;
      border-color: #818cf8;
      color: white;
      font-weight: 600;
    }
    .btn--primary:hover { background: #5558e6; border-color: #a5b4fc; }

    .overlay {
      position: fixed;
      inset: 0;
      background: rgba(0,0,0,0.6);
      display: grid;
      place-items: center;
      z-index: 100;
    }
    .overlay__panel {
      position: relative;
      background: #1e1e2e;
      border: 1px solid rgba(255,255,255,0.1);
      border-radius: 16px;
      width: min(960px, 94vw);
      max-height: 90vh;
      overflow-y: auto;
    }
    .overlay__close {
      position: absolute;
      top: 8px;
      right: 8px;
      background: rgba(255,255,255,0.06);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.12);
      border-radius: 6px;
      width: 28px;
      height: 28px;
      cursor: pointer;
      font-size: 18px;
      line-height: 1;
      z-index: 1;
    }
    .overlay__close:hover { background: rgba(255,255,255,0.12); }
    .create-dialog {
      background: #1e1e2e;
      border: 1px solid rgba(255,255,255,0.1);
      border-radius: 16px;
      padding: 32px;
      width: min(820px, 92vw);
      max-height: 90vh;
      overflow-y: auto;
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .create-dialog__title {
      margin: 0 0 12px;
      font-size: 22px;
      color: #f8fafc;
    }
    .create-dialog .field__textarea {
      min-height: 220px;
      resize: vertical;
    }
    .create-dialog--drag {
      box-shadow: 0 0 0 2px rgba(56,189,248,0.55), 0 24px 80px rgba(0,0,0,0.45);
      border-color: rgba(56,189,248,0.7);
    }
    .create-dialog__prompt-label {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 8px;
      margin-bottom: 4px;
    }
    .create-dialog__attach-btn {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.12);
      color: #cbd5e1;
      padding: 4px 10px;
      border-radius: 6px;
      font-size: 12px;
      font-weight: 600;
      cursor: pointer;
      transition: background 0.15s, color 0.15s, border-color 0.15s;
    }
    .create-dialog__attach-btn:hover {
      background: rgba(99,102,241,0.18);
      color: #ddd6fe;
      border-color: rgba(167,139,250,0.55);
    }
    .create-dialog__attachments {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin-top: 8px;
    }
    .create-dialog__attachment {
      position: relative;
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 4px;
      padding: 6px 8px 8px;
      background: rgba(0,0,0,0.25);
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 8px;
      max-width: 140px;
    }
    .create-dialog__attachment img {
      width: 120px;
      height: 80px;
      object-fit: cover;
      border-radius: 4px;
      border: 1px solid rgba(255,255,255,0.08);
    }
    .create-dialog__attachment-name {
      font-size: 11px;
      color: #94a3b8;
      max-width: 120px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .create-dialog__attachment-remove {
      position: absolute;
      top: 2px;
      right: 4px;
      width: 20px;
      height: 20px;
      border-radius: 999px;
      border: 0;
      background: rgba(0,0,0,0.55);
      color: #f8fafc;
      font-size: 14px;
      line-height: 1;
      cursor: pointer;
    }
    .create-dialog__attachment-remove:hover {
      background: rgba(239,68,68,0.7);
    }
    .create-dialog__attachment-error {
      margin-top: 6px;
      font-size: 12px;
      color: #fca5a5;
      background: rgba(239,68,68,0.10);
      border: 1px solid rgba(239,68,68,0.35);
      border-radius: 6px;
      padding: 6px 10px;
    }
    .create-dialog__file {
      display: none;
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
    .create-cli-picker {
      display: inline-flex;
      gap: 2px;
      padding: 2px;
      border: 1px solid rgba(255,255,255,0.1);
      border-radius: 8px;
      background: rgba(0,0,0,0.3);
    }
    .create-cli-picker__btn {
      border: 0;
      background: transparent;
      color: #94a3b8;
      padding: 5px 14px;
      font-size: 13px;
      border-radius: 6px;
      cursor: pointer;
      transition: background 0.15s, color 0.15s;
      display: inline-flex;
      align-items: center;
      gap: 6px;
    }
    .create-cli-picker__icon { font-size: 14px; line-height: 1; }
    .create-cli-picker__btn:hover { color: #e2e8f0; background: rgba(255,255,255,0.06); }
    .create-cli-picker__btn--active {
      background: rgba(99,102,241,0.22);
      color: #c7d2fe;
    }
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

    /* Layout fills the scroll container; the app__body now provides
       the scrollbar so the header and status bar stay pinned. */
    .layout {
      min-height: 100%;
      transition: all 0.3s ease;
      display: flex;
      flex-direction: column;
    }
    .layout--focus {
      padding: 12px;
    }
    .dashboard {
      display: flex;
      gap: 16px;
      padding: 16px;
      overflow-x: auto;
      flex: 1;
      min-width: 0;
    }
    .workspace {
      display: grid;
      grid-template-columns: var(--side-sheet-width, 280px) minmax(0, 1fr);
      gap: 16px;
      width: 100%;
      flex: 1 1 auto;
      min-height: 0;
      align-items: stretch;
      animation: slideIn 0.25s ease;
      position: relative;
    }
    .workspace--nav-collapsed {
      grid-template-columns: 36px minmax(0, 1fr);
      gap: 12px;
    }
    .task-nav.task-nav--collapsed {
      padding: 10px 4px;
      gap: 8px;
      min-width: 0;
      width: 36px;
      align-items: center;
      overflow: hidden;
    }
    .task-nav__header-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
    }
    .task-nav__collapse,
    .task-nav__expand {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.08);
      color: #cbd5e1;
      width: 28px;
      height: 28px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 16px;
      line-height: 1;
      display: grid;
      place-items: center;
    }
    .task-nav__collapse:hover,
    .task-nav__expand:hover {
      background: rgba(255,255,255,0.12);
      color: #f8fafc;
    }
    .task-nav__expand--board {
      margin-top: 4px;
    }
    .workspace__main {
      display: flex;
      min-width: 0;
      min-height: 0;
    }
    .workspace__main > app-job-detail {
      display: block;
      flex: 1;
      min-width: 0;
      min-height: 0;
    }
    .task-nav {
      background: #181825;
      border: 1px solid rgba(255,255,255,0.06);
      border-radius: 20px;
      padding: 20px;
      display: flex;
      flex-direction: column;
      gap: 18px;
      height: 100%;
      max-height: none;
      overflow: hidden;
      min-width: 200px;
      box-sizing: border-box;
      position: relative;
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
      cursor: pointer;
      user-select: none;
      transition: color 0.15s ease;
    }
    .task-nav__group-header:hover {
      color: #cbd5e1;
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
    .task-nav__item-project {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      color: var(--project-color, #94a3b8);
    }
    .task-nav__item-disk {
      display: inline-grid;
      place-items: center;
      width: 14px;
      height: 14px;
      border-radius: 999px;
      background: var(--project-color, #94a3b8);
      color: var(--project-on, #0b1020);
      font-size: 9px;
      font-weight: 800;
      flex: 0 0 auto;
    }
    .task-nav__group-toggle {
      display: inline-block;
      width: 12px;
      font-size: 10px;
      transition: transform 0.15s ease;
      margin-right: 4px;
    }
    .task-nav__group--collapsed .task-nav__group-toggle {
      transform: rotate(0deg);
    }
    .task-nav__group--collapsed .task-nav__items {
      display: none;
    }
    .task-nav__add {
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 5px;
      width: 100%;
      background: rgba(139, 92, 246, 0.06);
      border: 1px dashed rgba(139, 92, 246, 0.28);
      color: #a78bfa;
      padding: 7px 10px;
      border-radius: 10px;
      cursor: pointer;
      font-size: 12px;
      font-weight: 500;
      transition: background 0.15s, border-color 0.15s, color 0.15s;
    }
    .task-nav__add:hover {
      background: rgba(139, 92, 246, 0.16);
      border-color: rgba(139, 92, 246, 0.5);
      color: #c4b5fd;
    }
    .task-nav__resize-handle {
      position: absolute;
      top: 0;
      right: 0;
      width: 4px;
      height: 100%;
      cursor: col-resize;
      background: transparent;
      border-radius: 0 20px 20px 0;
      transition: background 0.15s ease;
    }
    .task-nav__resize-handle:hover {
      background: rgba(99, 102, 241, 0.3);
    }
    .task-nav__resize-handle--active {
      background: rgba(99, 102, 241, 0.5);
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
    
    :host {
      --resizing-cursor: col-resize;
    }
    
    ::ng-deep body.resizing {
      cursor: col-resize !important;
      user-select: none;
    }
    
    ::ng-deep body.resizing * {
      cursor: col-resize !important;
    }
  `]
})
export class App implements OnInit {
  readonly selectedJob = signal<JobDetail | null>(null);
  readonly showCreate = signal(false);
  /**
   * When non-null, names the project whose orchestrator feed is currently
   * open as an overlay. The toolbar button toggles this for the active
   * project; the overlay closes on backdrop click.
   */
  readonly orchFeedProject = signal<string | null>(null);
  /** When non-null, names the project whose detail panel is open. */
  readonly projectDetailName = signal<string | null>(null);
  readonly watchPaths = signal<WatchPathEntry[]>([]);
  readonly activeProjects = signal<Set<string>>(new Set(JSON.parse(localStorage.getItem('activeProjects') ?? '[]')));
  readonly sideSheetWidth = signal<number>(parseInt(localStorage.getItem('sideSheetWidth') ?? '280'));
  readonly collapsedGroups = signal<Set<string>>(new Set(JSON.parse(localStorage.getItem('collapsedGroups') ?? '[]')));
  readonly taskNavCollapsed = signal<boolean>(localStorage.getItem('taskNavCollapsed') === '1');

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
      archive: filterJobs(grouped.archive ?? []),
    } as GroupedJobs;
  });

  // The visible lane order is the canonical Order field, which is also what
  // ProjectRunner.GetNextReadyJob picks by. Keeping a single source of truth
  // here means "what's at the top of Ready runs first" is structurally true,
  // not just usually true.
  readonly displayGrouped = computed(() => this.filteredGrouped());

  readonly focusGroups = computed(() => {
    const grouped = this.displayGrouped();
    return [
      { state: '1-preparation', title: 'In Preparation', icon: '📋', jobs: grouped.preparation },
      { state: '2-ready', title: 'Ready', icon: '📦', jobs: grouped.ready },
      { state: '3-progress', title: 'In Progress', icon: '🔵', jobs: grouped.progress },
      { state: '4-review', title: 'Review', icon: '🟡', jobs: grouped.review },
      { state: '5-completed', title: 'Completed', icon: '🟢', jobs: grouped.completed },
      { state: '6-archive', title: 'Archive', icon: '🗄️', jobs: grouped.archive ?? [] }
    ];
  });
  readonly selectedJobUsesCopilot = computed(() => (this.selectedJob()?.info.cliType ?? 'copilot') === 'copilot');

  newTitle = '';
  newWatchPath = '';
  newAgent = 'copilot';
  newPrompt = '';
  newTargetState = '1-preparation';
  newCliType: CliType = readDefaultCliPref();
  newModel = readDefaultModelPref(readDefaultCliPref());
  newAttachments: PendingAttachment[] = [];

  readonly cliTypes = CLI_TYPES;
  readonly availableModels = signal<CliModelInfo[]>([]);
  
  private resizing = false;

  createDialogTitle(): string {
    switch (this.newTargetState) {
      case '2-ready': return 'Add Task to Ready';
      case '1-preparation': return 'Add Task to Preparation';
      default: return 'Add Task';
    }
  }

  cliTypeLabel(t: CliType): string { return fmtCliTypeLabel(t); }

  formatMultiplier(mult: number | null): string { return fmtMultiplier(mult); }

  onCreateCliTypeChange(t: CliType) {
    if (this.newCliType === t) return;
    this.newCliType = t;
    this.newModel = readDefaultModelPref(t);
    this.loadCreateModels(t);
  }

  /**
   * Status bar changed the default CLI for new tasks. Pre-fill the create
   * dialog so the next ＋ Add Task lands on the user's pick without making
   * them re-pick inside the dialog.
   */
  onDefaultCliChange(t: CliType): void {
    this.newCliType = t;
    this.newModel = readDefaultModelPref(t);
  }

  onDefaultModelChange(ev: { cliType: CliType; model: string }): void {
    if (ev.cliType === this.newCliType) {
      this.newModel = ev.model;
    }
  }

  private loadCreateModels(cliType: CliType) {
    this.jobService.getCliModelCatalog(cliType).subscribe({
      next: (catalog) => {
        const models = catalog.models ?? [];
        this.availableModels.set(models);
        if (!this.newModel) {
          const def = models.find(m => m.isDefault);
          if (def) this.newModel = def.id;
        }
      },
      error: () => this.availableModels.set([])
    });
  }

  canAddTaskToGroup(state: string): boolean {
    return state === '1-preparation' || state === '2-ready';
  }

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
    this.restoreDetailFromUrl();
  }

  private restoreDetailFromUrl() {
    const params = new URLSearchParams(window.location.search);
    const jobId = params.get('job');
    const watchPath = params.get('watchPath');
    if (jobId && watchPath) {
      this.jobService.getDetail(jobId, watchPath).subscribe({
        next: (detail) => this.selectedJob.set(detail),
        error: () => history.replaceState(null, '', window.location.pathname),
      });
    }
  }

  refresh() {
    this.jobService.refresh();
  }

  openDetail(job: JobInfo) {
    history.replaceState(null, '', `?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(job.watchPath)}`);
    this.jobService.getDetail(job.id, job.watchPath).subscribe({
      next: (detail) => this.selectedJob.set(detail),
      error: (err) => {
        history.replaceState(null, '', window.location.pathname);
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
    history.replaceState(null, '', window.location.pathname);
  }

  onCompleteAndNextReview() {
    const currentJobKey = this.selectedJob()?.info.jobKey;
    const reviewJobs = this.jobService.grouped().review.filter(j => j.jobKey !== currentJobKey);
    this.refresh();
    if (reviewJobs.length > 0) {
      this.openDetail(reviewJobs[0]);
    } else {
      this.closeDetail();
    }
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

  onArchiveAll() {
    const completed = this.filteredGrouped().completed;
    if (completed.length === 0) return;
    const moves = completed.map(job => this.jobService.moveJob(job.id, '6-archive', job.watchPath));
    forkJoin(moves).subscribe({
      next: () => this.refresh(),
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Failed to archive tasks',
          fallbackMessage: 'One or more tasks could not be moved to Archive',
          source: 'Archive all'
        });
        this.refresh();
      }
    });
  }

  openCreate(targetState?: string) {
    this.newTargetState = targetState === '2-ready' ? '2-ready' : '1-preparation';
    this.newWatchPath = this.pickCreateWatchPath();
    this.loadCreateModels(this.newCliType);
    this.showCreate.set(true);
  }

  /**
   * Toggle the orchestrator feed overlay. Picks the project to show by
   * preferring (1) the currently open detail's project, (2) the first
   * active project filter, (3) the first known watch path. Closes the
   * overlay if it is already open.
   */
  toggleOrchFeed(): void {
    if (this.orchFeedProject() != null) {
      this.closeOrchFeed();
      return;
    }
    const project = this.pickOrchFeedProject();
    if (project) this.orchFeedProject.set(project);
  }

  closeOrchFeed(): void {
    this.orchFeedProject.set(null);
  }

  /** Tooltip for the toolbar button; shows which project the feed will open for. */
  orchFeedTooltip(): string {
    const project = this.pickOrchFeedProject();
    return project
      ? `Open orchestrator feed for "${project}"`
      : 'No project selected';
  }

  /**
   * Project the orchestrator side sheet should align to. Tracks the
   * currently open detail's project so flipping into a task and then
   * opening the side sheet picks the right thread automatically. When
   * no detail is open, falls back to the first active or first known
   * project the same way the feed overlay does.
   */
  readonly orchSideSheetPreferredProject = computed<string | null>(() => {
    const detail = this.selectedJob();
    if (detail?.info?.projectName) return detail.info.projectName;
    const active = [...this.activeProjects()];
    if (active.length > 0) return active[0];
    const watchPaths = this.watchPaths();
    return watchPaths.length > 0 ? watchPaths[0].name : null;
  });

  orchChatTooltip(): string {
    const project = this.orchSideSheetPreferredProject();
    return project
      ? `Toggle orchestrator chat for "${project}"`
      : 'No project selected';
  }

  /**
   * Phase 5: orchestrator side sheet emitted "make a task from this".
   * Picks the watch path that matches the named project, opens the
   * existing create-task dialog with the orchestrator reply seeded into
   * the prompt, and lets a short heuristic title fall out of the first
   * non-empty line.
   */
  onCreateTaskFromOrchestratorDraft(event: { projectName: string; promptText: string }): void {
    const watchEntry = this.watchPaths().find((wp) => wp.name === event.projectName);
    if (!watchEntry) return;
    this.newTargetState = '1-preparation';
    this.newWatchPath = watchEntry.path;
    this.newPrompt = event.promptText;
    this.newTitle = deriveDraftTitle(event.promptText);
    this.loadCreateModels(this.newCliType);
    this.showCreate.set(true);
  }

  private pickOrchFeedProject(): string | null {
    const detail = this.selectedJob();
    if (detail?.info?.projectName) return detail.info.projectName;
    const active = [...this.activeProjects()];
    if (active.length > 0) return active[0];
    const watchPaths = this.watchPaths();
    return watchPaths.length > 0 ? watchPaths[0].name : null;
  }

  /** Open the project detail panel from a click on the project tab's ⚙ button. */
  openProjectDetail(name: string): void {
    this.projectDetailName.set(name);
  }

  closeProjectDetail(): void {
    this.projectDetailName.set(null);
  }

  /**
   * "Open feed" button inside the project detail panel: close the detail
   * panel, open the orchestrator feed for the same project. Two stacked
   * overlays would be confusing; swap instead.
   */
  onOpenFeedFromDetail(name: string): void {
    this.projectDetailName.set(null);
    this.orchFeedProject.set(name);
  }

  private pickCreateWatchPath(): string {
    const paths = this.watchPaths();
    if (paths.length === 0) return '';
    const last = localStorage.getItem('lastCreateWatchPath');
    const isValid = (p: string | null) => !!p && paths.some(wp => wp.path === p);
    const active = this.activeProjects();
    const activePaths = paths.filter(wp => active.has(wp.name));

    if (activePaths.length === 1) {
      return activePaths[0].path;
    }
    if (activePaths.length > 1) {
      const lastInActive = activePaths.find(wp => wp.path === last);
      if (lastInActive) return lastInActive.path;
      return activePaths[0].path;
    }
    if (isValid(last)) return last as string;
    return paths[0].path;
  }

  cancelCreate() {
    this.showCreate.set(false);
    this.newTitle = '';
    this.newPrompt = '';
    this.newAgent = 'copilot';
    this.newTargetState = '1-preparation';
    this.newCliType = readDefaultCliPref();
    this.newModel = readDefaultModelPref(this.newCliType);
    this.availableModels.set([]);
    for (const att of this.newAttachments) URL.revokeObjectURL(att.previewUrl);
    this.newAttachments = [];
  }

  submitCreate() {
    const attachments = this.newAttachments;
    const promptDraft = this.newPrompt.trim();
    const watchPath = this.newWatchPath;

    // When attachments are present we defer writing the prompt to the create
    // call (its `pending-attachment-…` placeholders are not yet resolvable),
    // upload each image against the new jobId, then PUT prompt.md with the
    // real `attachments/<file>` references.
    const initialPrompt = attachments.length > 0 ? undefined : (promptDraft || undefined);

    this.jobService.createJob({
      title: this.newTitle.trim(),
      watchPath,
      agent: this.newCliType,
      promptMarkdown: initialPrompt,
      targetState: this.newTargetState,
      cliType: this.newCliType,
      model: this.newModel.trim() || undefined
    }).subscribe({
      next: (res) => {
        localStorage.setItem('lastCreateWatchPath', watchPath);
        if (attachments.length > 0) {
          void this.uploadCreateAttachments(res.id, watchPath, promptDraft, attachments);
        }
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

  private async uploadCreateAttachments(
    jobId: string,
    watchPath: string,
    promptDraft: string,
    attachments: PendingAttachment[]
  ): Promise<void> {
    let prompt = promptDraft;
    for (const att of attachments) {
      try {
        const form = new FormData();
        form.append('file', att.file, att.file.name || `${att.alt}.png`);
        const url = `/api/jobs/${encodeURIComponent(jobId)}/attachments`
          + (watchPath ? `?watchPath=${encodeURIComponent(watchPath)}` : '');
        const res = await fetch(url, { method: 'POST', body: form });
        if (!res.ok) {
          this.errorDialog.show(new Error(`Upload failed (${res.status}) for ${att.file.name || att.alt}`), {
            title: 'Attachment upload failed',
            fallbackMessage: 'Could not upload one of the pasted images.',
            source: `Task ${jobId}`
          });
          continue;
        }
        const payload = (await res.json()) as { fileName: string; relativePath: string };
        prompt = prompt.replace(
          new RegExp(`pending-attachment-${att.id}`, 'g'),
          payload.relativePath
        );
      } catch (err) {
        this.errorDialog.show(err as Error, {
          title: 'Attachment upload failed',
          fallbackMessage: 'Could not upload one of the pasted images.',
          source: `Task ${jobId}`
        });
      }
    }

    this.jobService.updateJobFile(jobId, 'prompt.md', prompt, watchPath).subscribe({
      next: () => this.refresh(),
      error: (err) => this.errorDialog.show(err, {
        title: 'Failed to save prompt',
        fallbackMessage: 'Attachments uploaded, but writing prompt.md failed.',
        source: `Task ${jobId}`
      })
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

  // Pre-bound arrow-function aliases for child components that take a
  // predicate-style input (e.g. <app-project-tabs>). Using arrows keeps
  // `this` correct without per-call .bind().
  readonly isProjectActiveFn = (name: string) => this.isProjectActive(name);
  readonly getRunnerIndicatorFn = (name: string) => this.getRunnerIndicator(name);
  readonly getAutoInfoFn = (name: string) => this.getAutoInfo(name);
  readonly identityFor = (name: string) => projectIdentity(name);

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

  getAutoInfo(name: string): ProjectAutoInfo {
    const status = this.jobService.runnerStatus();
    const runner = status.projects[name];
    const mode = runner?.mode ?? 'manual';
    const readyCount = runner?.queuedJobIds.length ?? 0;
    const hasActive = !!runner?.activeJobId;

    if (mode === 'auto-continuous' || mode === 'auto-single') {
      return {
        state: 'on',
        readyCount,
        icon: '🔁',
        label: 'Auto',
        tooltip: readyCount > 0
          ? `Auto-pickup is on — when the current task finishes, the next Ready task starts automatically (${readyCount} waiting). Click to stop; the running task will continue, but no further tasks will be picked up.`
          : `Auto-pickup is on — the next task moved to Ready will start automatically. Click to stop; the running task (if any) will continue but no further tasks will be picked up.`
      };
    }

    if (mode === 'paused' && hasActive) {
      return {
        state: 'stopping',
        readyCount,
        icon: '⏸',
        label: 'Stopping',
        tooltip: `Auto-pickup stopped — the current task keeps running, but no more tasks will be picked up automatically. Click to resume auto-pickup.`
      };
    }

    return {
      state: 'off',
      readyCount,
      icon: '▶',
      label: 'Auto',
      tooltip: readyCount > 0
        ? `Enable auto-pickup — when the current task finishes, the next Ready task starts automatically (${readyCount} waiting).`
        : `Enable auto-pickup — as soon as a task moves to Ready, it will start automatically.`
    };
  }

  onToggleAuto(name: string) {
    const runner = this.jobService.runnerStatus().projects[name];
    const mode = runner?.mode ?? 'manual';
    const newMode = (mode === 'auto-continuous' || mode === 'auto-single') ? 'paused' : 'auto-continuous';
    this.jobService.setRunnerMode(name, newMode).subscribe({
      next: () => this.jobService.refreshRunnerStatus(true),
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Failed to change auto-pickup mode',
          fallbackMessage: 'Failed to change auto-pickup mode',
          source: `Project ${name}`
        });
      }
    });
  }

  onFileSaved() {
    // Re-fetch detail to reflect changes, and refresh the board so updates
    // (e.g. renamed titles) propagate to the card and task-nav views immediately.
    const current = this.selectedJob();
    if (current) {
      this.jobService.getDetail(current.info.id, current.info.watchPath).subscribe({
        next: (detail) => this.selectedJob.set(detail),
      });
    }
    this.jobService.refresh(true);
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
    if (!this.selectedJobUsesCopilot()) return;
    this.errorDialog.requestCliConfig();
  }

  // Side-sheet width and collapse functionality
  toggleGroupCollapse(state: string) {
    const current = new Set(this.collapsedGroups());
    if (current.has(state)) {
      current.delete(state);
    } else {
      current.add(state);
    }
    this.collapsedGroups.set(current);
    localStorage.setItem('collapsedGroups', JSON.stringify([...current]));
  }

  isGroupCollapsed(state: string): boolean {
    return this.collapsedGroups().has(state);
  }

  setTaskNavCollapsed(collapsed: boolean) {
    this.taskNavCollapsed.set(collapsed);
    localStorage.setItem('taskNavCollapsed', collapsed ? '1' : '0');
  }

  startResize(event: MouseEvent) {
    event.preventDefault();
    this.resizing = true;
    document.body.classList.add('resizing');

    const startX = event.clientX;
    const startWidth = this.sideSheetWidth();

    const onMouseMove = (e: MouseEvent) => {
      if (!this.resizing) return;
      const deltaX = e.clientX - startX;
      const newWidth = Math.max(200, startWidth + deltaX); // No maximum limit
      this.sideSheetWidth.set(newWidth);
    };

    const onMouseUp = () => {
      this.resizing = false;
      document.body.classList.remove('resizing');
      localStorage.setItem('sideSheetWidth', this.sideSheetWidth().toString());
      document.removeEventListener('mousemove', onMouseMove);
      document.removeEventListener('mouseup', onMouseUp);
    };

    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('mouseup', onMouseUp);
  }
}

function readDefaultCliPref(): CliType {
  const stored = localStorage.getItem('defaultCliType') as CliType | null;
  if (stored && (CLI_TYPES as string[]).includes(stored)) return stored;
  return 'copilot';
}

function readDefaultModelPref(cliType: CliType): string {
  return localStorage.getItem('defaultModel:' + cliType) ?? '';
}

/**
 * Best-effort task title from a Markdown reply: take the first non-empty
 * line, strip Markdown decoration, cap at 80 chars. Used by
 * `onCreateTaskFromOrchestratorDraft` so the user lands in the create
 * dialog with a placeholder title instead of an empty field.
 */
function deriveDraftTitle(text: string): string {
  if (!text) return '';
  for (const raw of text.split('\n')) {
    const line = raw.replace(/^#+\s*/, '').replace(/[*_`]/g, '').trim();
    if (line.length === 0) continue;
    return line.length > 80 ? line.slice(0, 77).trim() + '...' : line;
  }
  return '';
}
