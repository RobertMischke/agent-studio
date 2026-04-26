import { Component, input, output, signal, effect, OnDestroy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { JobDetail, WatchPathEntry, CliOutputLine, CliSettings, CliExecution, ContextUsageSnapshot } from '../models/job.model';
import { JobService } from '../services/job.service';
import { ErrorDialogService } from '../services/error-dialog.service';
import { CliConsoleComponent } from './cli-console';

@Component({
  selector: 'app-job-detail',
  standalone: true,
  imports: [FormsModule, CliConsoleComponent],
  template: `
    <div class="detail">
      <header class="detail__header">
        <div class="detail__header-main">
          <button class="detail__back" (click)="back.emit()">←</button>
          <div class="detail__headline">
            <div class="detail__eyebrow">Task focus</div>
            <h2 class="detail__title">{{ detail().info.title || detail().info.id }}</h2>
          </div>
        </div>
        <span class="detail__state" [class]="'state--' + detail().info.state">
          {{ stateLabel(detail().info.state) }}
        </span>
      </header>

      <div class="detail__meta">
        <label class="detail__meta-item detail__meta-item--project">
          <span class="detail__meta-label">Project</span>
          <select class="detail__project-select"
                  [ngModel]="detail().info.watchPath"
                  (ngModelChange)="onProjectChange($event)">
            @for (wp of watchPaths(); track wp.path) {
              <option [value]="wp.path">{{ wp.name }}</option>
            }
          </select>
        </label>
        <div class="detail__meta-item">
          <span class="detail__meta-label">Agent</span>
          <span class="detail__meta-value">🤖 {{ detail().info.agent }}</span>
        </div>
        <div class="detail__meta-item">
          <span class="detail__meta-label">Order</span>
          <span class="detail__meta-value">#{{ detail().info.order }}</span>
        </div>
        <div class="detail__meta-item">
          <span class="detail__meta-label">Created</span>
          <span class="detail__meta-value">{{ formatDate(detail().info.createdAt) }}</span>
        </div>
      </div>

      <div class="detail__tools">
        @if (canStartJob() || isRunning()) {
          <section class="sidebar-card sidebar-card--toolbar">
            <div class="sidebar-card__header">
              <div>
                <div class="section__eyebrow">Command deck</div>
                <h3 class="section__title">Agent controls</h3>
              </div>
            </div>
            <div class="execution-bar">
              @if (isRunning()) {
                <div class="execution-bar__status">
                  <span class="execution-bar__pulse"></span>
                  <span class="execution-bar__text">Running since {{ elapsedTime() }}</span>
                </div>
                <button class="btn-exec btn-exec--stop" (click)="stopJob()">⏹ Stop</button>
              } @else {
                <button class="btn-exec btn-exec--start" (click)="startJob()" [disabled]="starting()">
                  {{ starting() ? '⏳ Starting...' : '▶ Start CLI' }}
                </button>
              }
            </div>
          </section>
        }

        @if (detail().info.sessionName) {
          <section class="sidebar-card sidebar-card--toolbar">
            <div class="sidebar-card__header">
              <div>
                <div class="section__eyebrow">Copilot session</div>
                <h3 class="section__title">{{ detail().info.sessionName }}</h3>
              </div>
            </div>
            @if (detail().info.lastUsage; as usage) {
              <div class="session-usage">
                @if (usage.tokens) {
                  <div class="session-usage__row"><span class="session-usage__label">Tokens</span><span class="session-usage__value">{{ usage.tokens }}</span></div>
                }
                @if (usage.changes) {
                  <div class="session-usage__row"><span class="session-usage__label">Changes</span><span class="session-usage__value">{{ usage.changes }}</span></div>
                }
                @if (usage.requests) {
                  <div class="session-usage__row"><span class="session-usage__label">Requests</span><span class="session-usage__value">{{ usage.requests }}</span></div>
                }
              </div>
            }
            @if (!isRunning()) {
              <div class="session-followup">
                <textarea class="session-followup__input"
                          rows="3"
                          placeholder="Follow-up prompt — resumes the same Copilot session via --resume"
                          [value]="followupPrompt()"
                          (input)="followupPrompt.set($any($event.target).value)"></textarea>
                <button class="btn-exec btn-exec--start"
                        (click)="continueJob()"
                        [disabled]="continuing() || !followupPrompt().trim()">
                  {{ continuing() ? '⏳ Resuming...' : '↻ Continue session' }}
                </button>
              </div>
            }
          </section>
        }

        <section class="sidebar-card sidebar-card--toolbar">
          <div class="sidebar-card__header">
            <div>
              <div class="section__eyebrow">Context window</div>
              <h3 class="section__title">/context usage</h3>
            </div>
            <button class="btn-sm" (click)="refreshContextUsage()" [disabled]="refreshingContextUsage()">
              {{ refreshingContextUsage() ? '⏳ Refreshing...' : '↻ Refresh' }}
            </button>
          </div>

          @if (contextUsage(); as usage) {
            <div class="context-usage">
              <div class="context-usage__meta">
                <span class="context-usage__stamp">Updated {{ formatDateTime(usage.at) }}</span>
                @if (usage.status !== 'ok' && usage.error) {
                  <span class="context-usage__status context-usage__status--error">{{ usage.error }}</span>
                }
              </div>

              @if (usage.metrics.length > 0) {
                <div class="context-usage__metrics">
                  @for (metric of usage.metrics; track metric.label) {
                    <div class="context-usage__metric">
                      <span class="context-usage__metric-label">{{ metric.label }}</span>
                      <span class="context-usage__metric-value">{{ metric.value }}</span>
                    </div>
                  }
                </div>
              }

              @if (usage.sections.length > 0) {
                <div class="context-usage__sections">
                  @for (section of usage.sections; track section.title) {
                    <section class="context-usage__section">
                      <h4 class="context-usage__section-title">{{ section.title }}</h4>
                      <ul class="context-usage__list">
                        @for (item of section.items; track $index) {
                          <li>{{ item }}</li>
                        }
                      </ul>
                    </section>
                  }
                </div>
              }

              @if (usage.notes.length > 0) {
                <div class="context-usage__notes">
                  @for (note of usage.notes; track $index) {
                    <div class="context-usage__note">{{ note }}</div>
                  }
                </div>
              }
            </div>
          } @else {
            <div class="sidebar-card__empty">
              Trigger a refresh to capture and parse the current context usage for this task.
            </div>
          }
        </section>

        @if (showCliConfig()) {
          <section class="sidebar-card sidebar-card--toolbar">
            <div class="cli-config">
              <div class="cli-config__header">
                <span class="cli-config__title">🔧 CLI Configuration</span>
                <button class="detail__error-close" (click)="showCliConfig.set(false)">✕</button>
              </div>
              @if (cliStatus()) {
                <div class="cli-config__status" [class.cli-config__status--ok]="cliStatus()!.available"
                     [class.cli-config__status--err]="!cliStatus()!.available">
                  @if (cliStatus()!.available) {
                    ✅ {{ cliStatus()!.version }} — {{ cliStatus()!.path }}
                  } @else {
                    ❌ Not found at: {{ cliStatus()!.path }}
                  }
                </div>
              }
              <div class="cli-config__row">
                <input class="cli-config__input" type="text"
                       [value]="cliPathDraft()"
                       (input)="cliPathDraft.set($any($event.target).value)"
                       placeholder="z.B. copilot, C:\\Program Files\\GitHub\\copilot.exe" />
                <button class="btn-sm" (click)="testCliPath()" [disabled]="cliTesting()">
                  {{ cliTesting() ? '⏳' : '🧪 Test' }}
                </button>
                <button class="btn-sm btn-sm--primary" (click)="saveCliPath()" [disabled]="cliTesting()">💾 Save</button>
              </div>
              @if (cliTestResult(); as result) {
                <div class="cli-config__status" [class.cli-config__status--ok]="result.available"
                     [class.cli-config__status--err]="!result.available">
                  @if (result.available) {
                    ✅ Found: {{ result.version }}
                  } @else {
                    ❌ {{ result.version || 'Not found at this path' }}
                  }
                </div>
              }
              <div class="cli-config__separator"></div>
              <div class="cli-config__row">
                <input class="cli-config__input" [type]="showToken() ? 'text' : 'password'"
                       [value]="tokenDraft()"
                       (input)="tokenDraft.set($any($event.target).value)"
                       placeholder="GitHub Token (PAT or OAuth)" />
                <button class="btn-sm" (click)="showToken.set(!showToken())">
                  {{ showToken() ? '🙈' : '👁' }}
                </button>
                <button class="btn-sm btn-sm--primary" (click)="saveToken()" [disabled]="tokenSaving()">
                  {{ tokenSaving() ? '⏳' : '💾 Save Token' }}
                </button>
              </div>
              @if (cliStatus()) {
                <div class="cli-config__status" [class.cli-config__status--ok]="cliStatus()!.hasToken"
                     [class.cli-config__status--err]="!cliStatus()!.hasToken">
                  {{ cliStatus()!.hasToken ? '🔑 Token configured' : '⚠️ No token — CLI may fail to authenticate' }}
                </div>
              }
            </div>
          </section>
        }
      </div>

      <div class="detail__layout">
        <main class="detail__main">
          <section class="section section--primary section--fill">
            <div class="section__header">
              <div>
                <div class="section__eyebrow">Samurai view</div>
                <h3 class="section__title section__title--large">Task description</h3>
              </div>
              @if (!isProgress()) {
                @if (editingPrompt()) {
                  <div class="section__actions">
                    <button class="btn-sm" (click)="cancelEdit('prompt')">Cancel</button>
                    <button class="btn-sm btn-sm--primary" (click)="saveFile('prompt.md')">Save</button>
                  </div>
                } @else {
                  <button class="btn-sm" (click)="startEdit('prompt')">✏️ Edit</button>
                }
              }
            </div>

            @if (editingPrompt()) {
              <textarea class="section__editor section__editor--primary section__editor--fill" [(ngModel)]="promptDraftValue" rows="16"></textarea>
            } @else {
              <pre class="section__body section__body--primary section__body--scroll">{{ detail().promptMarkdown || '(empty)' }}</pre>
            }
          </section>
        </main>

        <aside class="detail__inspector">
          <section class="inspector">
            <div class="inspector__header">
              <div>
                <div class="section__eyebrow">Deep dive</div>
                <h3 class="section__title section__title--large">Agent protocol</h3>
              </div>
              <div class="inspector__tabs">
                <button class="inspector__tab"
                        [class.inspector__tab--active]="activeInspectorTab() === 'protocol'"
                        (click)="activeInspectorTab.set('protocol')">
                  Protocol
                </button>
                <button class="inspector__tab"
                        [class.inspector__tab--active]="activeInspectorTab() === 'activity'"
                        (click)="activeInspectorTab.set('activity')">
                  Activity
                </button>
              </div>
            </div>

            <div class="inspector__body">
              @if (activeInspectorTab() === 'protocol') {
                <section class="section section--fill">
                  <div class="section__header">
                    <div>
                      <div class="section__eyebrow">Agent notes</div>
                      <h3 class="section__title section__title--large">status.md</h3>
                    </div>
                    @if (!isProgress()) {
                      @if (editingStatus()) {
                        <div class="section__actions">
                          <button class="btn-sm" (click)="cancelEdit('status')">Cancel</button>
                          <button class="btn-sm btn-sm--primary" (click)="saveFile('status.md')">Save</button>
                        </div>
                      } @else {
                        <button class="btn-sm" (click)="startEdit('status')">✏️ Edit</button>
                      }
                    }
                  </div>
                  @if (editingStatus()) {
                    <textarea class="section__editor section__editor--fill" [(ngModel)]="statusDraftValue" rows="14"></textarea>
                  } @else {
                    <pre class="section__body section__body--scroll">{{ detail().statusMarkdown || '(empty)' }}</pre>
                  }
                </section>
              } @else {
                <div class="inspector__stack">
                  <section class="sidebar-card sidebar-card--panel">
                    <div class="sidebar-card__header">
                      <div>
                        <div class="section__eyebrow">Live stream</div>
                        <h3 class="section__title">CLI output</h3>
                      </div>
                      @if (cliOutput().length > 0 || detail().log.length > 0 || isRunning()) {
                        <button class="btn-sm" (click)="showLogOverlay.set(true)">⤢ Maximize log</button>
                      }
                    </div>

                    @if (cliOutput().length > 0 || isRunning()) {
                      <app-cli-console [lines]="cliOutput()" [title]="'CLI output'" [bodyMaxHeight]="'34vh'" />
                    } @else {
                      <div class="sidebar-card__empty">Start the task to follow the agent output live.</div>
                    }
                  </section>

                  <section class="sidebar-card sidebar-card--panel">
                    <div class="sidebar-card__header">
                      <div>
                        <div class="section__eyebrow">Timeline</div>
                        <h3 class="section__title">Event protocol</h3>
                      </div>
                    </div>
                    @if (detail().log.length > 0) {
                      <div class="log log--sidebar">
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
                    } @else {
                      <div class="sidebar-card__empty">No protocol entries yet.</div>
                    }
                  </section>
                </div>
              }
            </div>
          </section>
        </aside>
      </div>

      @if (showLogOverlay()) {
        <div class="log-overlay" (click)="showLogOverlay.set(false)">
          <div class="log-overlay__panel" (click)="$event.stopPropagation()">
            <div class="log-overlay__header">
              <div>
                <div class="section__eyebrow">Fullscreen</div>
                <h3 class="log-overlay__title">Agent log</h3>
              </div>
              <button class="btn-sm" (click)="showLogOverlay.set(false)">✕ Close</button>
            </div>

            <div class="log-overlay__content">
              <section class="sidebar-card">
                <div class="sidebar-card__header">
                  <h3 class="section__title">CLI output</h3>
                </div>
                <app-cli-console [lines]="cliOutput()" [title]="'CLI output'" [bodyMaxHeight]="'calc(100vh - 320px)'" />
              </section>

              <section class="sidebar-card">
                <div class="sidebar-card__header">
                  <h3 class="section__title">Protocol</h3>
                </div>
                @if (detail().log.length > 0) {
                  <div class="log log--overlay">
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
                } @else {
                  <div class="sidebar-card__empty">No protocol entries yet.</div>
                }
              </section>
            </div>
          </div>
        </div>
      }
    </div>
  `,
  styles: [`
    .session-usage {
      display: flex;
      flex-direction: column;
      gap: 4px;
      padding: 8px 12px;
      margin: 8px 0;
      font-size: 0.78rem;
      background: rgba(255,255,255,0.03);
      border-radius: 8px;
    }
    .session-usage__row { display: flex; justify-content: space-between; gap: 12px; }
    .session-usage__label { color: rgba(255,255,255,0.55); text-transform: uppercase; letter-spacing: 0.06em; font-size: 0.7rem; }
    .session-usage__value { color: #cdd6f4; font-family: var(--font-mono, monospace); }
    .session-followup { display: flex; flex-direction: column; gap: 8px; margin-top: 8px; }
    .session-followup__input {
      width: 100%;
      box-sizing: border-box;
      background: rgba(0,0,0,0.25);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 8px;
      padding: 8px 10px;
      font-family: inherit;
      font-size: 0.85rem;
      resize: vertical;
    }
    .session-followup__input:focus { outline: none; border-color: rgba(137,180,250,0.5); }
    .context-usage {
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .context-usage__meta {
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .context-usage__stamp {
      color: rgba(255,255,255,0.55);
      font-size: 0.72rem;
      text-transform: uppercase;
      letter-spacing: 0.06em;
    }
    .context-usage__status {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      width: fit-content;
      padding: 6px 10px;
      border-radius: 999px;
      font-size: 0.76rem;
    }
    .context-usage__status--error {
      background: rgba(239,68,68,0.14);
      color: #fca5a5;
      border: 1px solid rgba(239,68,68,0.24);
    }
    .context-usage__metrics {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
      gap: 10px;
    }
    .context-usage__metric {
      padding: 10px 12px;
      border-radius: 12px;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.05);
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .context-usage__metric-label {
      color: rgba(255,255,255,0.55);
      text-transform: uppercase;
      letter-spacing: 0.06em;
      font-size: 0.7rem;
    }
    .context-usage__metric-value {
      color: #e2e8f0;
      font-size: 0.84rem;
      line-height: 1.5;
      font-family: var(--font-mono, monospace);
    }
    .context-usage__sections {
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .context-usage__section {
      padding: 12px;
      border-radius: 12px;
      background: rgba(0,0,0,0.18);
      border: 1px solid rgba(255,255,255,0.05);
    }
    .context-usage__section-title {
      margin: 0 0 8px;
      color: #c4b5fd;
      font-size: 0.8rem;
      text-transform: uppercase;
      letter-spacing: 0.08em;
    }
    .context-usage__list {
      margin: 0;
      padding-left: 18px;
      color: #cbd5e1;
      font-size: 0.84rem;
      line-height: 1.6;
    }
    .context-usage__notes {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .context-usage__note {
      padding: 10px 12px;
      border-radius: 10px;
      background: rgba(255,255,255,0.03);
      color: #94a3b8;
      font-size: 0.82rem;
      line-height: 1.5;
    }
    .detail {
      background: #181825;
      border: 1px solid rgba(255,255,255,0.06);
      border-radius: 24px;
      padding: 24px;
      min-height: calc(100vh - 118px);
      display: flex;
      flex-direction: column;
      gap: 20px;
      position: relative;
    }

    .detail__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 16px;
    }
    .detail__header-main {
      display: flex;
      align-items: flex-start;
      gap: 14px;
      min-width: 0;
      flex: 1;
    }
    .detail__headline {
      min-width: 0;
    }
    .detail__eyebrow {
      font-size: 11px;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: #64748b;
      margin-bottom: 6px;
    }
    .detail__back {
      background: rgba(255,255,255,0.06);
      border: none;
      color: #94a3b8;
      width: 36px; height: 36px;
      border-radius: 10px;
      cursor: pointer;
      font-size: 16px;
      display: grid; place-items: center;
      flex-shrink: 0;
    }
    .detail__back:hover { background: rgba(255,255,255,0.1); }
    .detail__title {
      margin: 0;
      font-size: 28px;
      line-height: 1.2;
      color: #f8fafc;
      word-break: break-word;
    }
    .detail__state {
      font-size: 11px;
      text-transform: uppercase;
      padding: 7px 12px;
      border-radius: 999px;
      font-weight: 600;
      letter-spacing: 0.5px;
      flex-shrink: 0;
    }
    .state--1-preparation { background: rgba(139,92,246,0.15); color: #8b5cf6; }
    .state--2-ready { background: rgba(6,182,212,0.15); color: #06b6d4; }
    .state--3-progress { background: rgba(59,130,246,0.15); color: #3b82f6; }
    .state--4-review { background: rgba(245,158,11,0.15); color: #f59e0b; }
    .state--5-completed { background: rgba(16,185,129,0.15); color: #10b981; }

    .detail__meta {
      display: grid;
      grid-template-columns: minmax(180px, 1.4fr) repeat(3, minmax(110px, 1fr));
      gap: 12px;
    }
    .detail__meta-item {
      display: flex;
      flex-direction: column;
      gap: 6px;
      padding: 14px 16px;
      border-radius: 16px;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.05);
      min-width: 0;
    }
    .detail__meta-item--project {
      align-items: flex-start;
    }
    .detail__meta-label {
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      color: #64748b;
    }
    .detail__meta-value {
      font-size: 14px;
      color: #e2e8f0;
      font-weight: 600;
    }
    .detail__project-select {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.1);
      color: #e2e8f0;
      padding: 8px 10px;
      border-radius: 10px;
      font-size: 13px;
      cursor: pointer;
      width: 100%;
    }
    .detail__project-select:hover { border-color: rgba(255,255,255,0.2); }
    .detail__project-select:focus { outline: none; border-color: #6366f1; }

    .detail__tools {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
      gap: 16px;
    }

    .detail__layout {
      display: grid;
      grid-template-columns: minmax(0, 1.35fr) minmax(360px, 1.05fr);
      gap: 20px;
      align-items: start;
      min-height: 0;
      flex: 1;
    }
    .detail__main {
      display: flex;
      flex-direction: column;
      min-width: 0;
      min-height: 0;
    }
    .detail__inspector {
      display: flex;
      flex-direction: column;
      min-width: 0;
      min-height: 0;
    }
    .inspector {
      background: rgba(12,12,23,0.55);
      border: 1px solid rgba(255,255,255,0.05);
      border-radius: 24px;
      padding: 20px;
      display: flex;
      flex-direction: column;
      gap: 18px;
      min-height: 0;
      height: 100%;
    }
    .inspector__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 16px;
    }
    .inspector__tabs {
      display: flex;
      gap: 8px;
      flex-wrap: wrap;
      justify-content: flex-end;
    }
    .inspector__tab {
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.08);
      color: #94a3b8;
      padding: 8px 14px;
      border-radius: 999px;
      cursor: pointer;
      font-size: 12px;
      font-weight: 600;
      transition: background 0.15s ease, border-color 0.15s ease, color 0.15s ease;
    }
    .inspector__tab:hover {
      background: rgba(255,255,255,0.08);
      color: #e2e8f0;
    }
    .inspector__tab--active {
      background: rgba(99,102,241,0.2);
      border-color: rgba(99,102,241,0.4);
      color: #c4b5fd;
    }
    .inspector__body {
      display: flex;
      flex: 1;
      min-height: 0;
    }
    .inspector__body > * {
      flex: 1;
      min-height: 0;
      min-width: 0;
    }
    .inspector__stack {
      display: flex;
      flex-direction: column;
      gap: 16px;
      min-height: 0;
    }

    .section {
      margin: 0;
      background: rgba(12,12,23,0.55);
      border: 1px solid rgba(255,255,255,0.05);
      border-radius: 20px;
      padding: 20px;
    }
    .section--primary {
      padding: 24px;
    }
    .section--fill {
      display: flex;
      flex-direction: column;
      min-height: 0;
      height: 100%;
    }
    .section__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 12px;
      margin-bottom: 14px;
    }
    .section__eyebrow {
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      color: #64748b;
      margin-bottom: 4px;
    }
    .section__title {
      font-size: 13px;
      color: #cbd5e1;
      margin: 0;
    }
    .section__title--large {
      font-size: 20px;
      color: #f8fafc;
      line-height: 1.2;
    }
    .section__body {
      background: rgba(0,0,0,0.2);
      padding: 16px;
      border-radius: 14px;
      white-space: pre-wrap;
      word-break: break-word;
      font-size: 14px;
      line-height: 1.7;
      color: #cbd5e1;
      border: 1px solid rgba(255,255,255,0.04);
      margin: 0;
    }
    .section__body--primary {
      min-height: 320px;
      font-size: 15px;
      line-height: 1.8;
    }
    .section__body--scroll {
      flex: 1;
      min-height: 0;
      overflow: auto;
    }
    .log {
      display: flex;
      flex-direction: column;
      gap: 8px;
      min-width: 0;
    }
    .log--sidebar {
      max-height: none;
      min-height: 0;
      flex: 1;
      overflow-y: auto;
      padding-right: 4px;
    }
    .log--overlay {
      max-height: calc(100vh - 340px);
      overflow-y: auto;
      padding-right: 6px;
    }
    .log__row {
      display: flex;
      flex-wrap: wrap;
      gap: 12px;
      align-items: baseline;
      padding: 8px 12px;
      background: rgba(0,0,0,0.15);
      border-radius: 10px;
      font-size: 13px;
    }
    .log__time { font-size: 11px; color: #64748b; min-width: 70px; font-variant-numeric: tabular-nums; }
    .log__event { color: #e2e8f0; font-weight: 600; }
    .log__detail { color: #94a3b8; font-size: 12px; }

    .btn-sm {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.08);
      color: #94a3b8;
      padding: 4px 10px;
      border-radius: 6px;
      cursor: pointer;
      font-size: 12px;
    }
    .btn-sm:hover { background: rgba(255,255,255,0.1); }
    .btn-sm--primary { background: #6366f1; border-color: #6366f1; color: white; }
    .btn-sm--primary:hover { background: #5558e6; }
    .section__actions { display: flex; gap: 6px; }
    .section__editor {
      width: 100%;
      background: rgba(0,0,0,0.3);
      border: 1px solid rgba(99,102,241,0.4);
      color: #e2e8f0;
      padding: 16px;
      border-radius: 8px;
      font-family: 'Consolas', monospace;
      font-size: 13px;
      line-height: 1.6;
      resize: vertical;
      box-sizing: border-box;
    }
    .section__editor:focus { outline: none; border-color: #6366f1; }
    .section__editor--primary { min-height: 360px; }
    .section__editor--fill {
      flex: 1;
      min-height: 0;
      resize: none;
    }

    .execution-bar {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 12px;
    }
    .execution-bar__status {
      display: flex;
      align-items: center;
      gap: 10px;
      font-size: 13px;
      color: #94a3b8;
    }
    .execution-bar__pulse {
      width: 8px; height: 8px;
      border-radius: 50%;
      background: #3b82f6;
      animation: pulse 1.5s infinite;
    }
    @keyframes pulse {
      0%, 100% { opacity: 1; box-shadow: 0 0 0 0 rgba(59,130,246,0.4); }
      50% { opacity: 0.7; box-shadow: 0 0 0 6px rgba(59,130,246,0); }
    }
    .execution-bar__text { font-variant-numeric: tabular-nums; }
    .btn-exec {
      border: none;
      padding: 6px 16px;
      border-radius: 6px;
      cursor: pointer;
      font-size: 13px;
      font-weight: 600;
    }
    .btn-exec--start {
      background: rgba(34,197,94,0.15);
      color: #4ade80;
      border: 1px solid rgba(34,197,94,0.3);
    }
    .btn-exec--start:hover { background: rgba(34,197,94,0.25); }
    .btn-exec--stop {
      background: rgba(239,68,68,0.15);
      color: #f87171;
      border: 1px solid rgba(239,68,68,0.3);
    }
    .btn-exec--stop:hover { background: rgba(239,68,68,0.25); }
    .btn-exec:disabled { opacity: 0.5; cursor: wait; }

    .sidebar-card {
      background: rgba(12,12,23,0.55);
      border: 1px solid rgba(255,255,255,0.05);
      border-radius: 20px;
      padding: 18px;
      display: flex;
      flex-direction: column;
      gap: 14px;
      min-width: 0;
    }
    .sidebar-card--toolbar {
      min-height: 100%;
    }
    .sidebar-card--panel {
      flex: 1;
      min-height: 0;
    }
    .sidebar-card__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 12px;
    }
    .sidebar-card__empty {
      padding: 18px 16px;
      border-radius: 12px;
      background: rgba(255,255,255,0.03);
      color: #94a3b8;
      font-size: 13px;
      line-height: 1.5;
    }
    .detail__error-close {
      background: none;
      border: none;
      color: #f87171;
      cursor: pointer;
      font-size: 14px;
      padding: 2px 6px;
      border-radius: 4px;
      flex-shrink: 0;
    }
    .detail__error-close:hover { background: rgba(239,68,68,0.15); }

    .cli-config {
      animation: fadeIn 0.2s ease;
    }
    .cli-config__header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin-bottom: 10px;
    }
    .cli-config__title {
      font-size: 13px;
      font-weight: 600;
      color: #c4b5fd;
    }
    .cli-config__row {
      display: flex;
      gap: 8px;
      align-items: center;
    }
    .cli-config__input {
      flex: 1;
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.12);
      color: #e2e8f0;
      padding: 6px 10px;
      border-radius: 6px;
      font-size: 13px;
      font-family: 'Cascadia Code', 'Fira Code', monospace;
    }
    .cli-config__input:focus {
      outline: none;
      border-color: #6366f1;
    }
    .cli-config__status {
      font-size: 12px;
      padding: 6px 10px;
      border-radius: 6px;
      margin-top: 8px;
    }
    .cli-config__status--ok {
      background: rgba(34,197,94,0.1);
      color: #4ade80;
    }
    .cli-config__status--err {
      background: rgba(239,68,68,0.1);
      color: #fca5a5;
    }
    .cli-config__separator {
      border-top: 1px solid rgba(255,255,255,0.08);
      margin: 8px 0 4px;
    }
    .log-overlay {
      position: fixed;
      inset: 0;
      background: rgba(3,5,10,0.82);
      backdrop-filter: blur(6px);
      display: grid;
      place-items: center;
      padding: 24px;
      z-index: 200;
    }
    .log-overlay__panel {
      width: min(1200px, calc(100vw - 48px));
      max-height: calc(100vh - 48px);
      overflow: hidden;
      background: #11111b;
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 24px;
      padding: 24px;
      display: flex;
      flex-direction: column;
      gap: 18px;
      box-shadow: 0 24px 80px rgba(0,0,0,0.55);
    }
    .log-overlay__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 16px;
    }
    .log-overlay__title {
      margin: 0;
      font-size: 26px;
      color: #f8fafc;
    }
    .log-overlay__content {
      display: grid;
      grid-template-columns: minmax(0, 1.15fr) minmax(320px, 0.85fr);
      gap: 18px;
      min-height: 0;
      overflow: auto;
      padding-right: 4px;
    }
    @keyframes fadeIn {
      from { opacity: 0; transform: translateY(-4px); }
      to { opacity: 1; transform: translateY(0); }
    }
    @media (max-width: 1200px) {
      .detail__meta {
        grid-template-columns: repeat(2, minmax(0, 1fr));
      }
      .detail__layout,
      .log-overlay__content {
        grid-template-columns: 1fr;
      }
      .inspector__header {
        flex-direction: column;
      }
      .inspector__tabs {
        justify-content: flex-start;
      }
    }
    @media (max-width: 720px) {
      .detail {
        padding: 18px;
      }
      .detail__header {
        flex-direction: column;
      }
      .detail__title {
        font-size: 24px;
      }
      .detail__meta {
        grid-template-columns: 1fr;
      }
      .detail__tools {
        grid-template-columns: 1fr;
      }
      .execution-bar {
        flex-direction: column;
        align-items: stretch;
      }
      .cli-config__row {
        flex-wrap: wrap;
      }
      .log-overlay {
        padding: 12px;
      }
      .log-overlay__panel {
        width: calc(100vw - 24px);
        max-height: calc(100vh - 24px);
        padding: 16px;
      }
    }
  `]
})
export class JobDetailComponent implements OnDestroy {
  readonly detail = input.required<JobDetail>();
  readonly watchPaths = input<WatchPathEntry[]>([]);
  readonly back = output<void>();
  readonly fileSaved = output<void>();
  readonly projectChanged = output<string>();

  readonly editingPrompt = signal(false);
  readonly editingStatus = signal(false);
  readonly cliOutput = signal<CliOutputLine[]>([]);
  readonly isRunning = signal(false);
  readonly startedAt = signal<Date | null>(null);
  readonly elapsedTime = signal('');
  readonly errorMsg = signal<string | null>(null);
  readonly starting = signal(false);
  readonly continuing = signal(false);
  readonly followupPrompt = signal('');
  readonly showCliConfig = signal(false);
  readonly cliStatus = signal<CliSettings | null>(null);
  readonly cliPathDraft = signal('');
  readonly cliTestResult = signal<CliSettings | null>(null);
  readonly cliTesting = signal(false);
  readonly showLogOverlay = signal(false);
  readonly activeInspectorTab = signal<'protocol' | 'activity'>('protocol');
  readonly tokenDraft = signal('');
  readonly showToken = signal(false);
  readonly tokenSaving = signal(false);
  readonly contextUsage = signal<ContextUsageSnapshot | null>(null);
  readonly refreshingContextUsage = signal(false);

  promptDraftValue = '';
  statusDraftValue = '';
  private elapsedTimer: ReturnType<typeof setInterval> | null = null;
  private pollGeneration = 0;
  private pollTimeout: ReturnType<typeof setTimeout> | null = null;
  private contextUsageTimeout: ReturnType<typeof setTimeout> | null = null;
  private lastCliConfigRequest = 0;

  constructor(private jobService: JobService, private errorDialog: ErrorDialogService) {}

  private detailEffect = effect(() => {
    const d = this.detail();
    this.errorMsg.set(null);
    this.showLogOverlay.set(false);
    this.activeInspectorTab.set('protocol');
    this.showCliConfig.set(false);
    this.cliTestResult.set(null);
    this.editingPrompt.set(false);
    this.editingStatus.set(false);
    this.cliOutput.set([]);
    this.contextUsage.set(d.contextUsage);
    this.refreshingContextUsage.set(false);
    this.isRunning.set(false);
    this.startedAt.set(null);
    this.elapsedTime.set('0s');
    this.pollGeneration += 1;
    if (this.pollTimeout) {
      clearTimeout(this.pollTimeout);
      this.pollTimeout = null;
    }
    if (this.elapsedTimer) {
      clearInterval(this.elapsedTimer);
      this.elapsedTimer = null;
    }
    if (this.contextUsageTimeout) {
      clearTimeout(this.contextUsageTimeout);
      this.contextUsageTimeout = null;
    }
    this.applyExecutionState(d.info.execution);
    if (d.info.state === '3-progress' || d.info.execution?.status === 'running') {
      if (d.info.execution?.status === 'running' && !this.pollTimeout) {
        this.pollOutput();
      }
      // Try to load existing output
      this.jobService.getJobOutput(d.info.id, d.info.watchPath).subscribe({
        next: (output) => {
          if (output.length > 0) {
            this.cliOutput.set(output);
            if (!this.startedAt()) {
              this.startedAt.set(new Date());
            }
            if (!this.elapsedTimer && this.isRunning()) {
              this.startElapsedTimer();
            }
          }
        },
        error: (err) => {
          if (err.status !== 0) return; // silent for 404 etc
          this.showError(err);
        }
      });
    }

    this.scheduleContextUsageRefresh(!d.contextUsage);
  });
  private cliConfigEffect = effect(() => {
    const requestId = this.errorDialog.cliConfigRequest();
    if (requestId === 0 || requestId === this.lastCliConfigRequest) {
      return;
    }

    this.lastCliConfigRequest = requestId;
    this.openCliConfig();
  });

  ngOnDestroy() {
    this.isRunning.set(false);
    this.detailEffect.destroy();
    this.cliConfigEffect.destroy();
    if (this.pollTimeout) {
      clearTimeout(this.pollTimeout);
      this.pollTimeout = null;
    }
    if (this.elapsedTimer) {
      clearInterval(this.elapsedTimer);
      this.elapsedTimer = null;
    }
    if (this.contextUsageTimeout) {
      clearTimeout(this.contextUsageTimeout);
      this.contextUsageTimeout = null;
    }
  }

  canStartJob(): boolean {
    const state = this.detail().info.state;
    return (state === '2-ready' || state === '3-progress') && !this.isRunning();
  }

  startJob(): void {
    this.errorMsg.set(null);
    this.starting.set(true);
    this.jobService.startJob(this.detail().info.id, this.detail().info.watchPath).subscribe({
      next: (exec) => {
        this.starting.set(false);
        this.isRunning.set(true);
        this.startedAt.set(new Date(exec.startedAt));
        this.cliOutput.set([]);
        this.startElapsedTimer();
        this.pollOutput();
      },
      error: (err) => {
        this.starting.set(false);
        this.showError(err);
      }
    });
  }

  stopJob(): void {
    this.errorMsg.set(null);
    this.jobService.stopJob(this.detail().info.id, this.detail().info.watchPath).subscribe({
      next: () => {
        this.isRunning.set(false);
        if (this.pollTimeout) {
          clearTimeout(this.pollTimeout);
          this.pollTimeout = null;
        }
        if (this.elapsedTimer) {
          clearInterval(this.elapsedTimer);
          this.elapsedTimer = null;
        }
      },
      error: (err) => this.showError(err)
    });
  }

  continueJob(): void {
    const prompt = this.followupPrompt().trim();
    if (!prompt) return;

    this.errorMsg.set(null);
    this.continuing.set(true);
    this.jobService.continueJob(this.detail().info.id, prompt, this.detail().info.watchPath).subscribe({
      next: (exec) => {
        this.continuing.set(false);
        this.followupPrompt.set('');
        this.isRunning.set(true);
        this.startedAt.set(new Date(exec.startedAt));
        this.cliOutput.set([]);
        this.startElapsedTimer();
        this.pollOutput();
      },
      error: (err) => {
        this.continuing.set(false);
        this.showError(err);
      }
    });
  }

  private showError(err: any): void {
    const message = err.status === 0
      ? 'Backend not reachable — is the API running on localhost:5030?'
      : err.error?.error || (typeof err.error === 'string' ? err.error : `Request failed (${err.status || 'unknown'}): ${err.statusText || err.message || 'Unknown error'}`);

    this.errorMsg.set(message);
    this.errorDialog.show(err, {
      title: 'Task action failed',
      fallbackMessage: message,
      source: `Task ${this.detail().info.id}`,
      canOpenCliConfig: this.isCliErrorMessage(message)
    });
  }

  private applyExecutionState(execution: CliExecution | null): void {
    if (!execution) {
      return;
    }

    if (execution.status === 'running') {
      this.isRunning.set(true);
      this.startedAt.set(new Date(execution.startedAt));
      if (!this.elapsedTimer) {
        this.startElapsedTimer();
      }
      return;
    }

    this.isRunning.set(false);
    if (this.elapsedTimer) {
      clearInterval(this.elapsedTimer);
      this.elapsedTimer = null;
    }
    if (execution.status === 'failed') {
      const message = execution.exitCode === null
        ? 'Task execution failed.'
        : `Task execution failed with exit code ${execution.exitCode}.`;

      this.errorMsg.set(message);
      this.errorDialog.show(message, {
        title: 'Task execution failed',
        fallbackMessage: message,
        source: `Task ${this.detail().info.id}`,
        output: {
          execution,
          cliOutput: this.cliOutput()
        }
      });
    }
  }

  private startElapsedTimer(): void {
    if (this.elapsedTimer) clearInterval(this.elapsedTimer);
    this.updateElapsed();
    this.elapsedTimer = setInterval(() => this.updateElapsed(), 1000);
  }

  private updateElapsed(): void {
    const start = this.startedAt();
    if (!start) { this.elapsedTime.set('0s'); return; }
    const secs = Math.floor((Date.now() - start.getTime()) / 1000);
    if (secs < 60) this.elapsedTime.set(`${secs}s`);
    else if (secs < 3600) this.elapsedTime.set(`${Math.floor(secs / 60)}m ${secs % 60}s`);
    else this.elapsedTime.set(`${Math.floor(secs / 3600)}h ${Math.floor((secs % 3600) / 60)}m`);
  }

  private pollOutput(): void {
    const generation = this.pollGeneration;
    const poll = () => {
      if (!this.isRunning() || generation !== this.pollGeneration) {
        return;
      }
      this.jobService.getJobOutput(this.detail().info.id, this.detail().info.watchPath).subscribe({
        next: (output) => {
          if (generation !== this.pollGeneration) {
            return;
          }
          this.cliOutput.set(output);
          this.pollTimeout = setTimeout(poll, 2000);
        },
        error: () => {
          this.pollTimeout = setTimeout(poll, 5000);
        }
      });
    };
    this.pollTimeout = setTimeout(poll, 1000);
  }

  isProgress(): boolean {
    return this.detail().info.state === '3-progress';
  }

  startEdit(which: 'prompt' | 'status') {
    if (which === 'prompt') {
      this.promptDraftValue = this.detail().promptMarkdown ?? '';
      this.editingPrompt.set(true);
    } else {
      this.statusDraftValue = this.detail().statusMarkdown ?? '';
      this.editingStatus.set(true);
    }
  }

  cancelEdit(which: 'prompt' | 'status') {
    if (which === 'prompt') this.editingPrompt.set(false);
    else this.editingStatus.set(false);
  }

  saveFile(fileName: string) {
    const content = fileName === 'prompt.md' ? this.promptDraftValue : this.statusDraftValue;
    this.jobService.updateJobFile(this.detail().info.id, fileName, content, this.detail().info.watchPath).subscribe({
      next: () => {
        if (fileName === 'prompt.md') this.editingPrompt.set(false);
        else this.editingStatus.set(false);
        this.fileSaved.emit();
      },
      error: (err) => this.showError(err)
    });
  }

  stateLabel(state: string): string {
    return state.replace(/^\d+-/, '');
  }

  formatTime(dateStr: string): string {
    return new Date(dateStr).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  formatDate(dateStr: string): string {
    return new Date(dateStr).toLocaleDateString();
  }

  formatDateTime(dateStr: string): string {
    return new Date(dateStr).toLocaleString([], {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit'
    });
  }

  isCliError(): boolean {
    const msg = this.errorMsg();
    return this.isCliErrorMessage(msg);
  }

  openCliConfig(): void {
    this.showCliConfig.set(true);
    this.cliTestResult.set(null);
    this.jobService.getCliSettings().subscribe({
      next: (settings) => {
        this.cliStatus.set(settings);
        this.cliPathDraft.set(settings.path);
      },
      error: (err) => this.showError(err)
    });
  }

  dismissError(): void {
    this.errorMsg.set(null);
    this.showCliConfig.set(false);
  }

  testCliPath(): void {
    const path = this.cliPathDraft().trim();
    if (!path) return;
    this.cliTesting.set(true);
    this.cliTestResult.set(null);
    this.jobService.testCliPath(path).subscribe({
      next: (result) => {
        this.cliTestResult.set(result);
        this.cliTesting.set(false);
      },
      error: (err) => {
        this.cliTesting.set(false);
        this.showError(err);
      }
    });
  }

  saveCliPath(): void {
    const path = this.cliPathDraft().trim();
    if (!path) return;
    this.cliTesting.set(true);
    this.jobService.setCliPath(path).subscribe({
      next: (result) => {
        this.cliStatus.set(result);
        this.cliTestResult.set(null);
        this.cliTesting.set(false);
        if (result.available) {
          this.errorMsg.set(null);
          this.showCliConfig.set(false);
        }
      },
      error: (err) => {
        this.cliTesting.set(false);
        this.showError(err);
      }
    });
  }

  saveToken(): void {
    const token = this.tokenDraft().trim();
    if (!token) return;
    this.tokenSaving.set(true);
    this.jobService.setGitHubToken(token).subscribe({
      next: (result) => {
        this.cliStatus.set(result);
        this.tokenSaving.set(false);
        this.tokenDraft.set('');
        if (result.hasToken && result.available) {
          this.errorMsg.set(null);
        }
      },
      error: (err) => {
        this.tokenSaving.set(false);
        this.showError(err);
      }
    });
  }

  onProjectChange(targetWatchPath: string) {
    if (targetWatchPath === this.detail().info.watchPath) return;
    this.jobService.changeProject(this.detail().info.id, targetWatchPath, this.detail().info.watchPath).subscribe({
      next: () => this.projectChanged.emit(targetWatchPath),
      error: (err) => this.showError(err)
    });
  }

  refreshContextUsage(silent = false): void {
    if (this.refreshingContextUsage()) {
      return;
    }

    this.refreshingContextUsage.set(true);
    this.jobService.refreshContextUsage(this.detail().info.id, this.detail().info.watchPath).subscribe({
      next: (usage) => {
        this.contextUsage.set(usage);
        this.refreshingContextUsage.set(false);
        this.scheduleContextUsageRefresh(false);
      },
      error: (err) => {
        this.refreshingContextUsage.set(false);
        this.scheduleContextUsageRefresh(false);
        if (!silent) {
          this.showError(err);
        }
      }
    });
  }

  private scheduleContextUsageRefresh(immediate: boolean): void {
    if (this.contextUsageTimeout) {
      clearTimeout(this.contextUsageTimeout);
    }

    const delay = immediate ? 1200 : this.isRunning() ? 60000 : 180000;
    this.contextUsageTimeout = setTimeout(() => this.refreshContextUsage(true), delay);
  }

  private isCliErrorMessage(message: string | null | undefined): boolean {
    return !!message && /cli|copilot|authenticat/i.test(message);
  }
}
