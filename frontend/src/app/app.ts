import { Component, computed, effect, OnInit, signal, untracked } from '@angular/core';
import { forkJoin } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { JobColumnComponent } from './components/job-column';
import { JobDetailComponent } from './components/job-detail';
import { CliUsageSheetComponent } from './components/cli-usage-sheet';
import { JobService } from './services/job.service';
import { JobDetail, JobInfo, GroupedJobs, WatchPathEntry, CliType, CLI_TYPES, CliModelInfo } from './models/job.model';
import { ErrorDialogService } from './services/error-dialog.service';
import { cliTypeLabel as fmtCliTypeLabel, formatMultiplier as fmtMultiplier } from './services/format.util';

@Component({
  selector: 'app-root',
  imports: [JobColumnComponent, JobDetailComponent, CliUsageSheetComponent, FormsModule],
  template: `
    <div class="app">
      <header class="header">
        <div class="header__brand">
          <img class="header__icon" src="icons/icon.svg" alt="Agent Task Board" width="32" height="32" />
          <h1 class="header__title">
            <span class="header__title-ai">Agent</span><span class="header__title-sep"></span><span class="header__title-name">Task Board</span>
          </h1>
        </div>
        <div class="header__filters">
          @for (name of projectNames(); track name) {
            <button class="filter-chip"
                    [attr.data-testid]="'project-filter-' + name"
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
          <button class="btn" (click)="usageSheet.toggle()" title="CLI sessions">
            🪙 Usage
          </button>
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
            <app-job-column title="In Preparation" icon="📋" state="1-preparation" [jobs]="filteredGrouped().preparation" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" (addTask)="openCreate($event)" />
            <app-job-column title="Ready" icon="📦" state="2-ready" [jobs]="filteredGrouped().ready" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" (addTask)="openCreate($event)" />
            <app-job-column title="In Progress" icon="🔵" state="3-progress" [jobs]="filteredGrouped().progress" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" />
            <app-job-column title="Review" icon="🟡" state="4-review" [jobs]="filteredGrouped().review" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" />
            <app-job-column title="Completed" icon="🟢" state="5-completed" [jobs]="filteredGrouped().completed" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" (archiveAll)="onArchiveAll()" />
            <app-job-column title="Archive" icon="🗄️" state="6-archive" [jobs]="filteredGrouped().archive" (jobClick)="openDetail($event)" (jobDrop)="onJobDrop($event)" (jobReorder)="onJobReorder($event)" />
          </main>
        }
      </div>

        <app-cli-usage-sheet #usageSheet class="app__sidesheet" />
      </div>

      @if (showCreate()) {
        <div class="overlay" (click)="cancelCreate()">
          <div class="create-dialog" (click)="$event.stopPropagation()">
            <h2 class="create-dialog__title">{{ createDialogTitle() }}</h2>
            <label class="field">
              <span class="field__label">Title</span>
              <input class="field__input" [(ngModel)]="newTitle" placeholder="Task title" />
            </label>
            <label class="field">
              <span class="field__label">Project</span>
              <select class="field__input" data-testid="create-project-select" [(ngModel)]="newWatchPath">
                @for (wp of watchPaths(); track wp.path) {
                  <option [value]="wp.path">{{ wp.name }}</option>
                }
              </select>
            </label>
            <div class="field">
              <span class="field__label">Agent</span>
              <div class="create-cli-picker">
                @for (t of cliTypes; track t) {
                  <button type="button"
                          class="create-cli-picker__btn"
                          [class.create-cli-picker__btn--active]="newCliType === t"
                          (click)="onCreateCliTypeChange(t)">
                    {{ cliTypeLabel(t) }}
                  </button>
                }
              </div>
            </div>
            <div class="field">
              <span class="field__label">Model</span>
              <select class="field__input" [(ngModel)]="newModel">
                <option value="">(default — CLI chooses)</option>
                @for (m of availableModels(); track m.id) {
                  <option [value]="m.id">{{ m.label }}{{ m.multiplier !== null && m.multiplier !== undefined ? ' — ' + formatMultiplier(m.multiplier) : '' }}{{ m.isDefault ? ' ✓' : '' }}</option>
                }
              </select>
            </div>
            <label class="field">
              <span class="field__label">Prompt (optional)</span>
              <textarea class="field__input field__textarea" [(ngModel)]="newPrompt" rows="10" placeholder="Task description..."></textarea>
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
      display: flex;
      flex-direction: column;
    }
    /* Body row holds the main layout and the CLI Usage sidesheet side-by-side.
       The sheet's :host width animates from 0 to its open width, so the layout
       reflows around it instead of being covered by an overlay. */
    .app__body {
      flex: 1 1 auto;
      display: flex;
      flex-direction: row;
      align-items: stretch;
      min-height: 0;
    }
    .app__body > .layout { flex: 1 1 auto; min-width: 0; }
    .app__sidesheet { align-self: stretch; }
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
    .header__icon {
      width: 32px;
      height: 32px;
      display: block;
      border-radius: 8px;
      box-shadow: 0 2px 8px rgba(99,102,241,0.35);
    }
    .header__title {
      margin: 0;
      font-size: 22px;
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
      text-shadow: 0 0 22px rgba(167,139,250,0.35);
      padding-right: 4px;
    }
    .header__title-sep {
      width: 2px;
      height: 20px;
      align-self: center;
      margin: 0 10px;
      border-radius: 2px;
      background: linear-gradient(180deg, rgba(129,140,248,0.0), rgba(129,140,248,0.85), rgba(129,140,248,0.0));
    }
    .header__title-name {
      font-weight: 600;
      letter-spacing: 0.02em;
      color: #e2e8f0;
      text-transform: uppercase;
      font-size: 18px;
    }
    .header__subtitle { font-size: 13px; color: #64748b; }
    .header__actions { display: flex; gap: 12px; }
    .header__filters { display: flex; gap: 8px; align-items: center; }
    .filter-chip {
      background: rgba(255,255,255,0.10);
      border: 1px solid rgba(255,255,255,0.20);
      color: #e2e8f0;
      padding: 5px 14px;
      border-radius: 20px;
      cursor: pointer;
      font-size: 12px;
      font-weight: 600;
      transition: all 0.15s;
    }
    .filter-chip:hover { background: rgba(255,255,255,0.18); border-color: rgba(255,255,255,0.30); color: #ffffff; }
    .filter-chip--active {
      background: rgba(139,92,246,0.45);
      border-color: rgba(167,139,250,0.85);
      color: #ffffff;
      box-shadow: 0 0 0 1px rgba(167,139,250,0.25), 0 2px 6px rgba(139,92,246,0.30);
    }
    .filter-chip--active:hover { background: rgba(139,92,246,0.6); }
    .runner-dot { font-size: 10px; margin-right: 2px; }
    .runner-dot--running { animation: pulse-runner 1.5s infinite; }
    @keyframes pulse-runner {
      0%, 100% { opacity: 1; }
      50% { opacity: 0.4; }
    }
    .btn {
      background: rgba(255,255,255,0.10);
      border: 1px solid rgba(255,255,255,0.20);
      color: #f8fafc;
      padding: 8px 16px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 13px;
      font-weight: 600;
      transition: background 0.15s, border-color 0.15s;
    }
    .btn:hover { background: rgba(255,255,255,0.18); border-color: rgba(255,255,255,0.30); }
    .btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .btn--create {
      background: rgba(139,92,246,0.45);
      border-color: rgba(167,139,250,0.85);
      color: #ffffff;
      box-shadow: 0 2px 8px rgba(139,92,246,0.30);
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
    }
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

    .layout {
      min-height: calc(100vh - 70px);
      transition: all 0.3s ease;
    }
    .layout--focus {
      padding: 24px;
      display: flex;
      min-height: calc(100vh - 70px);
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
      grid-template-columns: var(--side-sheet-width, 280px) minmax(0, 1fr);
      gap: 24px;
      width: 100%;
      height: calc(100vh - 118px);
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

  readonly focusGroups = computed(() => {
    const grouped = this.filteredGrouped();
    return [
      { state: '1-preparation', title: 'In Preparation', icon: '📋', jobs: grouped.preparation },
      { state: '2-ready', title: 'Ready', icon: '📦', jobs: grouped.ready },
      { state: '3-progress', title: 'In Progress', icon: '🔵', jobs: grouped.progress },
      { state: '4-review', title: 'Review', icon: '🟡', jobs: grouped.review },
      { state: '5-completed', title: 'Completed', icon: '🟢', jobs: grouped.completed },
      { state: '6-archive', title: 'Archive', icon: '🗄️', jobs: grouped.archive ?? [] }
    ];
  });

  newTitle = '';
  newWatchPath = '';
  newAgent = 'copilot';
  newPrompt = '';
  newTargetState = '1-preparation';
  newCliType: CliType = 'copilot';
  newModel = '';

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
    this.newModel = '';
    this.loadCreateModels(t);
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
    this.newCliType = 'copilot';
    this.newModel = '';
    this.availableModels.set([]);
  }

  submitCreate() {
    this.jobService.createJob({
      title: this.newTitle.trim(),
      watchPath: this.newWatchPath,
      agent: this.newCliType,
      promptMarkdown: this.newPrompt.trim() || undefined,
      targetState: this.newTargetState,
      cliType: this.newCliType,
      model: this.newModel.trim() || undefined
    }).subscribe({
      next: () => {
        localStorage.setItem('lastCreateWatchPath', this.newWatchPath);
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
